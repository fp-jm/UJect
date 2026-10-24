using System;

namespace UJect
{
    public readonly struct ContainerId : IEquatable<ContainerId>
    {
        public readonly ushort ID;

        public ContainerId(ushort id)
        {
            this.ID = id;
        }

        public bool Equals(ContainerId other) => ID == other.ID;

        public override bool Equals(object? obj)
        {
            return obj is ContainerId other && Equals(other);
        }

        public override int GetHashCode() => ID.GetHashCode();

        public static bool operator ==(ContainerId left, ContainerId right) => left.Equals(right);

        public static bool operator !=(ContainerId left, ContainerId right) => !left.Equals(right);

        public ContainerId Next()
        {
            var next = ID + 1;
            if (next > ushort.MaxValue) throw new ArgumentOutOfRangeException($"Next ID is too high! Must be less than {ushort.MaxValue}");
            return new ContainerId((ushort)next);
        }

        public override string ToString()
        {
            return $"CID:{ID}";
        }
    }
}