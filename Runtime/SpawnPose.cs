using UnityEngine;

namespace MeshPresso
{
    /// <summary>
    /// Where a generated model will be placed, captured the moment generation starts.
    /// Generation takes a minute or two, so the pose is frozen up front rather than
    /// read at placement time — otherwise a moving camera or anchor would drag the
    /// result somewhere the user never asked for.
    /// </summary>
    public struct SpawnPose
    {
        public Vector3 Position;
        public Quaternion Rotation;

        public static SpawnPose At(Vector3 position, Quaternion rotation)
        {
            return new SpawnPose { Position = position, Rotation = rotation };
        }

        public static SpawnPose Default => At(Vector3.zero, Quaternion.identity);

        public override string ToString()
        {
            return $"pos {Position}, rot {Rotation.eulerAngles}";
        }
    }
}
