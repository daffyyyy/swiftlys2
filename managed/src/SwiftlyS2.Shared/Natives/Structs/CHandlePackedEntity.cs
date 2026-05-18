using System.Runtime.InteropServices;
using SwiftlyS2.Core.EntitySystem;
using SwiftlyS2.Shared.SchemaDefinitions;
using SwiftlyS2.Shared.Schemas;

namespace SwiftlyS2.Shared.Natives;

[StructLayout(LayoutKind.Sequential, Size = 4)]
public struct CHandlePackedEntity<T>( uint raw ) : ICHandle where T : class, ISchemaClass<T>
{
    public uint Raw { get; set; } = raw;
    public readonly uint EntityIndex => Raw & 0x3FFF;
    public readonly uint SerialNumber => (Raw >> 14) & 0x3FF;
    public readonly bool IsValid => EntityManager.GetEntityByIndex(EntityIndex) != null;

    public T? Value {
        readonly get {
            unsafe
            {
                if (!IsValid) return null;

                var ent = EntityManager.GetEntityByIndex(EntityIndex);
                if (ent == null) return null;

                return ent is T entity ? entity : T.From(ent.Address);
            }
        }
        set {
            if (value == null) Raw = 0xFFFFFF;
            else if (value is not CEntityInstance ent) throw new InvalidOperationException($"Value must be of type {typeof(T).Name} which implements CEntityInstance.");
            else Raw = ent.Entity == null ? 0xFFFFFF : ent.Entity.EntityHandle.Raw;
        }
    }

    public static CHandlePackedEntity<T> Invalid => new(0xFFFFFF);

    public static bool operator ==( CHandlePackedEntity<T> left, CHandlePackedEntity<T> right ) => left.Equals(right);
    public static bool operator !=( CHandlePackedEntity<T> left, CHandlePackedEntity<T> right ) => !left.Equals(right);
    public static implicit operator T( CHandlePackedEntity<T> handle ) => handle.Value ?? throw new InvalidOperationException("Entity handle is invalid or entity does not exist.");

    public readonly bool Equals( CHandlePackedEntity<T>? other ) => other.HasValue && other.Value.Raw == this.Raw;
    public override readonly bool Equals( object? obj ) => obj is CHandlePackedEntity<T> v && Equals(v);
    public override readonly int GetHashCode() => this.Raw.GetHashCode();
    public override readonly string ToString() => this.IsValid ? $"CHandlePackedEntity<{typeof(T).Name}>[{this.EntityIndex}] SerialNumber:{this.SerialNumber}" : $"CHandlePackedEntity<{typeof(T).Name}> invalid";
}
