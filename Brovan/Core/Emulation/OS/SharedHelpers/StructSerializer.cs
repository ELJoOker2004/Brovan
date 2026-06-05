using System.Text;
using static Brovan.Core.Helpers.BinaryHelpers;

namespace Brovan.Core.Emulation.OS
{
    public enum PointerContentType { UnicodeString, AsciiString, ByteArray, Struct }

    [AttributeUsage(AttributeTargets.Field)]
    public class EmulatedPointerAttribute : Attribute
    {
        public PointerContentType ContentType { get; set; }
        public EmulatedPointerAttribute(PointerContentType ContentType) => this.ContentType = ContentType;
    }

    [AttributeUsage(AttributeTargets.Field)]
    public class EmulatedInlineAttribute : Attribute
    {
        public int Size { get; }
        public bool Ascii { get; set; }
        public EmulatedInlineAttribute(int Size) => this.Size = Size;
    }

    public enum WriteStructError
    {
        None, NullDestination, DestinationNotMapped, PointerAllocationFailed,
        PointerDestinationNotMapped, UnsupportedFieldType, InvalidStructLayout,
        RecursionDepthExceeded, StringAllocationFailed
    }

    public sealed class WriteStructResult
    {
        public static readonly WriteStructResult Ok = new(true, WriteStructError.None, null);
        public bool Success { get; }
        public WriteStructError Error { get; }
        public string Detail { get; }

        private WriteStructResult(bool Success, WriteStructError Error, string Detail)
        {
            this.Success = Success;
            this.Error = Error;
            this.Detail = Detail;
        }

        public static WriteStructResult Fail(WriteStructError Error, string Detail = null) => new(false, Error, Detail);

        public override string ToString() => Success ? "OK" : $"FAIL({Error}){(Detail != null ? ": " + Detail : "")}";
    }

    /// <summary>
    /// Serializes managed structs to and from raw emulated-memory bytes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The per-struct field marshalling (layout, sizes, <see cref="EmulatedInlineAttribute"/> handling,
    /// nested structs and explicit <c>[FieldOffset]</c> layouts) is produced at compile time by the
    /// Brovan.SourceGen source generator (<c>Brovan.Generated.BrovanGeneratedMarshal</c>) instead of runtime
    /// reflection, so this type is NativeAOT/trimming/WebAssembly friendly. The byte layout it produces is
    /// identical to the previous reflection-based implementation.
    /// </para>
    /// <para>
    /// Marshallers are generated for every struct reachable from a <see cref="WriteStruct{T}"/>,
    /// <see cref="ParseStruct{T}(BinaryEmulator, ulong, out T)"/> or <see cref="GetStructSize{T}(BinaryEmulator)"/>
    /// call site (plus the structs they nest). A struct whose fields the generator cannot marshal raises a
    /// BRVGEN001 build error rather than failing silently at runtime; a type with no generated marshaller
    /// is reported through the normal failure result.
    /// </para>
    /// </remarks>
    public static class StructSerializer
    {
        /// <summary>
        /// Get a struct size.
        /// </summary>
        /// <typeparam name="T">Struct</typeparam>
        /// <param name="Emulator">The emulator instance to determine architecture.</param>
        /// <returns>return the size.</returns>
        public static uint GetStructSize<T>(BinaryEmulator Emulator) where T : struct
        {
            bool Is64 = Emulator._binary.Architecture == BinaryArchitecture.x64;
            return (uint)GetStructSize<T>(Is64);
        }

        /// <summary>
        /// Get a struct size.
        /// </summary>
        /// <typeparam name="T">Struct</typeparam>
        /// <param name="Is64">An indicator whether the architecture is x64.</param>
        /// <returns>return the size.</returns>
        public static int GetStructSize<T>(bool Is64) where T : struct
        {
            return Brovan.Generated.BrovanGeneratedMarshal.TryGetSize(typeof(T), Is64, out int Size) ? Size : 0;
        }

        /// <summary>
        /// Serializes <paramref name="Value"/> into emulated memory at <paramref name="Address"/>.
        /// </summary>
        /// <param name="Emulator">The emulator instance to write into.</param>
        /// <param name="Address">The emulated address to write the struct to.</param>
        /// <param name="Value">The struct value to serialize.</param>
        /// <returns>Returns <see cref="WriteStructResult.Ok"/> on success, otherwise a failure result describing the error.</returns>
        public static WriteStructResult WriteStruct<T>(BinaryEmulator Emulator, ulong Address, T Value) where T : struct
        {
            if (Address == 0)
                return WriteStructResult.Fail(WriteStructError.NullDestination, $"Destination address is NULL for {typeof(T).Name}");

            bool Is64 = Emulator._binary.Architecture == BinaryArchitecture.x64;

            using MemoryStream Ms = new();
            using BinaryWriter Bw = new(Ms, Encoding.Unicode, leaveOpen: true);

            if (!Brovan.Generated.BrovanGeneratedMarshal.TryWriteFields(Bw, Value, typeof(T), Is64, out WriteStructResult R))
                return WriteStructResult.Fail(WriteStructError.UnsupportedFieldType, $"No generated marshaller for {typeof(T).FullName}");
            if (!R.Success) return R;

            Bw.Flush();
            byte[] Bytes = Ms.ToArray();

            if (!Emulator.IsRegionMapped(Address, (ulong)Bytes.Length))
                return WriteStructResult.Fail(WriteStructError.DestinationNotMapped, $"{typeof(T).Name} (0x{Bytes.Length:X} bytes) at 0x{Address:X} is not fully mapped");
            if (!Emulator.WriteMemory(Address, Bytes))
                return WriteStructResult.Fail(WriteStructError.DestinationNotMapped, $"WriteMemory failed for {typeof(T).Name} at 0x{Address:X}");

            return WriteStructResult.Ok;
        }

        /// <summary>
        /// Deserializes a struct of type <typeparamref name="T"/> from emulated memory at <paramref name="Address"/>.
        /// </summary>
        /// <param name="Emulator">The emulator instance to read from.</param>
        /// <param name="Address">The emulated address to read the struct from.</param>
        /// <param name="Value">The deserialized struct value on success.</param>
        /// <returns>Returns true if successful, otherwise false.</returns>
        public static bool ParseStruct<T>(BinaryEmulator Emulator, ulong Address, out T Value) where T : struct
        {
            Value = default;
            if (Address == 0) return false;

            bool Is64 = Emulator._binary.Architecture == BinaryArchitecture.x64;
            if (!Brovan.Generated.BrovanGeneratedMarshal.TryGetSize(typeof(T), Is64, out int Size) || Size <= 0)
                return false;

            byte[] Raw = Emulator.ReadMemory(Address, (uint)Size);
            if (Raw == null || Raw.Length != Size) return false;

            object Boxed = Value;
            if (!Brovan.Generated.BrovanGeneratedMarshal.TryReadFields(Raw, 0, ref Boxed, typeof(T), Is64, out bool Ok) || !Ok)
                return false;

            Value = (T)Boxed;
            return true;
        }

        /// <summary>
        /// Deserializes a struct of type <typeparamref name="T"/> from a byte array directly./>.
        /// </summary>
        /// <param name="Emulator">The emulator instance to read from.</param>
        /// <param name="Data">The data to deserialize the struct.</param>
        /// <param name="Value">The deserialized struct value on success.</param>
        /// <returns>Returns true if successful, otherwise false.</returns>
        /// <remarks>
        /// This is mostly used in the fuzzing, this is never used in the emulator itself.
        /// </remarks>
        public static bool ParseStruct<T>(BinaryEmulator Emulator, byte[] Data, out T Value) where T : struct
        {
            Value = default;

            bool Is64 = Emulator._binary.Architecture == BinaryArchitecture.x64;
            if (!Brovan.Generated.BrovanGeneratedMarshal.TryGetSize(typeof(T), Is64, out int Size) || Size <= 0)
                return false;

            if (Data == null || Data.Length != Size) return false;

            object Boxed = Value;
            if (!Brovan.Generated.BrovanGeneratedMarshal.TryReadFields(Data, 0, ref Boxed, typeof(T), Is64, out bool Ok) || !Ok)
                return false;

            Value = (T)Boxed;
            return true;
        }
    }
}
