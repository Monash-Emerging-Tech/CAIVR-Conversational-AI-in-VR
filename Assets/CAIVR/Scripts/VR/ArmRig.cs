using UnityEngine;

namespace CAIVR.VR
{
    /// <summary>
    /// How far one arm is moved away from its resting pose. Everything is in degrees and zero is "as she sits".
    /// </summary>
    public struct ArmPose
    {
        /// <summary>Upper arm swings forward from the shoulder.</summary>
        public float Shoulder;

        /// <summary>Upper arm swings out to the side.</summary>
        public float Abduct;

        /// <summary>Forearm lifts about the elbow, raising the hand.</summary>
        public float Lift;

        /// <summary>Forearm swings out to the side about the elbow.</summary>
        public float Spread;

        /// <summary>Forearm turns so the palm faces up and inwards.</summary>
        public float Turn;

        /// <summary>Wrist bends the hand upwards.</summary>
        public float Wrist;

        /// <summary>Fingers straighten, 0 = as relaxed at rest, 1 = open and extended.</summary>
        public float Open;

        public static ArmPose Lerp(in ArmPose a, in ArmPose b, float t) => new ArmPose
        {
            Shoulder = Mathf.LerpUnclamped(a.Shoulder, b.Shoulder, t),
            Abduct = Mathf.LerpUnclamped(a.Abduct, b.Abduct, t),
            Lift = Mathf.LerpUnclamped(a.Lift, b.Lift, t),
            Spread = Mathf.LerpUnclamped(a.Spread, b.Spread, t),
            Turn = Mathf.LerpUnclamped(a.Turn, b.Turn, t),
            Wrist = Mathf.LerpUnclamped(a.Wrist, b.Wrist, t),
            Open = Mathf.LerpUnclamped(a.Open, b.Open, t),
        };

        public static ArmPose operator *(in ArmPose a, float s) => new ArmPose
        {
            Shoulder = a.Shoulder * s, Abduct = a.Abduct * s, Lift = a.Lift * s, Spread = a.Spread * s,
            Turn = a.Turn * s, Wrist = a.Wrist * s, Open = a.Open * s,
        };
    }

    /// <summary>
    /// One of the professor's arms, from collarbone to fingertips, and how to move it.
    ///
    /// The model's bones do not share axes (a bone's own "forward" is rarely the model's), so nothing
    /// here rotates a bone about its own axes. Arm movements are rotations about the world's
    /// left-right, up and forward directions as seen from the professor, and for each finger joint the
    /// hinge direction is measured from the skeleton rather than assumed: bend it a little, see which
    /// way the fingertip went, and keep the direction that curls towards the palm.
    ///
    /// Used twice: the scene builder uses it to bake a relaxed resting hand into the scene, and
    /// <see cref="ProfessorBody"/> uses it every frame to move the arm from that rest.
    /// </summary>
    public sealed class ArmRig
    {
        /// <summary>One finger: three joints, each with its measured hinge and the curl already in the rest pose.</summary>
        public sealed class Finger
        {
            public readonly Transform[] Joint = new Transform[3];
            public readonly Quaternion[] Rest = new Quaternion[3];
            public readonly Vector3[] Axis = new Vector3[3];
            public float[] Baked = new float[3];
        }

        // Relaxed curl, in degrees, at the three joints of each finger. Index to pinky then thumb. The
        // fingers curl more towards the little finger, which is what a hand at ease does.
        static readonly string[] FingerNames = { "Index", "Mid", "Ring", "Pinky", "Thumb" };
        static readonly float[][] RestCurl =
        {
            new[] { 12f, 16f, 8f },
            new[] { 16f, 22f, 10f },
            new[] { 20f, 26f, 12f },
            new[] { 24f, 28f, 14f },
            new[] { 4f, 8f, 6f },
        };

        public readonly char Side;
        public readonly float Sign;                      // +1 on her right, -1 on her left
        public readonly Transform Clavicle, Upper, Fore, Hand;
        public readonly Finger[] Fingers;

        Quaternion _clavicleRest, _upperRest, _foreRest, _handRest;
        readonly Transform _root;
        float _turnSign = 1f;

        public static ArmRig Create(BoneMap bones, char side, Transform root)
        {
            var clavicle = bones.Side(side, "Clavicle");
            var upper = bones.Side(side, "Upperarm");
            var fore = bones.Side(side, "Forearm");
            var hand = bones.Side(side, "Hand");

            if (clavicle == null || upper == null || fore == null || hand == null) return null;
            return new ArmRig(bones, side, root, clavicle, upper, fore, hand);
        }

        ArmRig(BoneMap bones, char side, Transform root, Transform clavicle, Transform upper, Transform fore, Transform hand)
        {
            Side = side;
            Sign = side == 'R' ? 1f : -1f;
            _root = root;
            Clavicle = clavicle; Upper = upper; Fore = fore; Hand = hand;

            Fingers = new Finger[FingerNames.Length];
            for (var f = 0; f < FingerNames.Length; f++)
            {
                // The pose saved in the scene already has this curl in it (see RelaxFingers).
                var finger = new Finger { Baked = (float[])RestCurl[f].Clone() };
                var complete = true;

                for (var j = 0; j < 3; j++)
                {
                    finger.Joint[j] = bones.Side(side, FingerNames[f] + (j + 1));
                    if (finger.Joint[j] == null) complete = false;
                }

                if (complete) Fingers[f] = finger;
            }

            CaptureRest();
            MeasureHinges();
            MeasureTurn();
        }

        void CaptureRest()
        {
            _clavicleRest = Clavicle.localRotation;
            _upperRest = Upper.localRotation;
            _foreRest = Fore.localRotation;
            _handRest = Hand.localRotation;

            foreach (var finger in Fingers)
            {
                if (finger == null) continue;
                for (var j = 0; j < 3; j++) finger.Rest[j] = finger.Joint[j].localRotation;
            }
        }

        // --- measuring the skeleton ------------------------------------------

        /// <summary>
        /// For each finger joint, the hinge direction in the joint's own space for which a positive angle
        /// curls the finger towards the palm. The palm faces down when she is sat with her hands on the table.
        /// </summary>
        void MeasureHinges()
        {
            var palm = Vector3.down;

            foreach (var finger in Fingers)
            {
                if (finger == null) continue;

                var j1 = finger.Joint[0];
                var j2 = finger.Joint[1];
                var j3 = finger.Joint[2];

                finger.Axis[0] = Hinge(j1, j2, palm);
                finger.Axis[1] = Hinge(j2, j3, palm);

                // The last joint has nothing past it to watch, but a finger's joints hinge the same way.
                finger.Axis[2] = j3.InverseTransformDirection(j2.TransformDirection(finger.Axis[1]));
            }
        }

        static Vector3 Hinge(Transform joint, Transform child, Vector3 palm)
        {
            var along = (child.position - joint.position).normalized;
            var worldAxis = Vector3.Cross(along, palm);
            if (worldAxis.sqrMagnitude < 0.0001f) worldAxis = Vector3.right;

            var axis = joint.InverseTransformDirection(worldAxis.normalized);

            var rest = joint.localRotation;
            var before = child.position;
            joint.localRotation = rest * Quaternion.AngleAxis(10f, axis);
            var moved = child.position - before;
            joint.localRotation = rest;

            return Vector3.Dot(moved, palm) < 0f ? -axis : axis;
        }

        /// <summary>
        /// Which way a positive turn of the forearm rotates the palm up. Measured by turning it a little
        /// and watching whether the line across the knuckles tips towards the sky.
        /// </summary>
        void MeasureTurn()
        {
            var index = Fingers[0]?.Joint[0];
            var pinky = Fingers[3]?.Joint[0];
            if (index == null || pinky == null) return;

            var axis = (Hand.position - Fore.position).normalized;
            var rest = Fore.rotation;
            var before = (index.position - pinky.position).y;

            Fore.rotation = Quaternion.AngleAxis(10f, axis) * rest;
            var after = (index.position - pinky.position).y;
            Fore.rotation = rest;

            _turnSign = after >= before ? 1f : -1f;
        }

        // --- baking the resting hand (scene builder) -------------------------

        /// <summary>
        /// Gives the hand the loose curl of a hand at ease, and keeps it as the resting pose. Without it the
        /// fingers stay as straight as the model's modelling pose, which looks stiff.
        /// </summary>
        public void RelaxFingers()
        {
            for (var f = 0; f < Fingers.Length; f++)
            {
                var finger = Fingers[f];
                if (finger == null) continue;

                for (var j = 0; j < 3; j++)
                {
                    var curl = RestCurl[f][j];
                    finger.Baked[j] = curl;
                    finger.Joint[j].localRotation = finger.Rest[j] * Quaternion.AngleAxis(curl, finger.Axis[j]);
                    finger.Rest[j] = finger.Joint[j].localRotation;
                }
            }
        }

        /// <summary>How much of the hand is below a height, measured from the finger tips, knuckles and wrist.</summary>
        public float Sink(float height)
        {
            var lowest = Hand.position.y - 0.012f;

            foreach (var finger in Fingers)
            {
                if (finger == null) continue;
                lowest = Mathf.Min(lowest, finger.Joint[2].position.y - 0.007f);
                lowest = Mathf.Min(lowest, finger.Joint[0].position.y - 0.009f);
            }

            return height - lowest;
        }

        /// <summary>The distance the elbow has to swing the hand, for turning "this much lower" into degrees.</summary>
        public float Lever => Mathf.Max(0.15f, (Hand.position - Fore.position).magnitude + 0.07f);

        /// <summary>
        /// Raises the forearm, if the hand is below the table, until it just rests on top. The result is
        /// kept as the new resting pose. For the scene builder.
        /// </summary>
        public void RestOnTable(float tableTop)
        {
            for (var pass = 0; pass < 6; pass++)
            {
                var sink = Sink(tableTop + 0.002f);
                if (sink <= 0.0005f) break;

                var degrees = Mathf.Rad2Deg * sink / Lever;
                Fore.rotation = Quaternion.AngleAxis(-degrees, _root.right) * Fore.rotation;
            }

            _foreRest = Fore.localRotation;
        }

        // --- moving the arm --------------------------------------------------

        /// <param name="pose">How far from rest to put each part of the arm.</param>
        /// <param name="extraLift">Extra forearm lift, degrees, on top of the pose (used to keep the hand off the table).</param>
        /// <param name="shrug">Collarbone lift, degrees, for breathing.</param>
        /// <param name="fingerCurl">Added to every finger joint, degrees, for the fidgeting of a hand at rest. Positive curls.</param>
        public void Apply(in ArmPose pose, float extraLift, float shrug, float fingerCurl, float fingerPhase)
        {
            var right = _root.right;
            var up = _root.up;
            var forward = _root.forward;

            Clavicle.localRotation = _clavicleRest;
            Clavicle.rotation = Quaternion.AngleAxis(Sign * shrug, forward) * Clavicle.rotation;

            Upper.localRotation = _upperRest;
            Upper.rotation = Quaternion.AngleAxis(Sign * pose.Abduct, forward)
                           * Quaternion.AngleAxis(-pose.Shoulder, right) * Upper.rotation;

            // Turning the forearm about its own length first, then swinging it, is what a real arm does.
            Fore.localRotation = _foreRest;
            var length = (Hand.position - Fore.position).normalized;
            Fore.rotation = Quaternion.AngleAxis(-(pose.Lift + extraLift), right)
                          * Quaternion.AngleAxis(Sign * pose.Spread, up)
                          * Quaternion.AngleAxis(pose.Turn * _turnSign, length) * Fore.rotation;

            Hand.localRotation = _handRest;
            Hand.rotation = Quaternion.AngleAxis(-pose.Wrist, right) * Hand.rotation;

            for (var f = 0; f < Fingers.Length; f++)
            {
                var finger = Fingers[f];
                if (finger == null) continue;

                // The little finger fidgets most and the index least.
                var wobble = fingerCurl * (0.6f + 0.25f * f)
                           * Mathf.Sin(Time.time * (0.9f + 0.37f * f) + fingerPhase + f * 1.7f);

                for (var j = 0; j < 3; j++)
                {
                    // Opening the hand takes out the relaxed curl and then a touch more.
                    var degrees = wobble - pose.Open * (finger.Baked[j] + 5f);
                    finger.Joint[j].localRotation = finger.Rest[j] * Quaternion.AngleAxis(degrees, finger.Axis[j]);
                }
            }
        }
    }
}
