// Array-backed replacements for Unity.Collections NativeArray<T> / FixedListNNBytes<T> used by the ported solver.
//
// NativeArray<T> is a struct holding a managed array reference, so (like the real thing) copies of the
// struct share storage. FixedList*Bytes<T> are *value types* in Unity; here they are structs holding a
// managed array, so a struct copy SHARES storage instead of copying it. The ported solver only mutates
// these lists through the owning struct (by ref / in place), so this is equivalent for this code base
// (see PORTING_NOTES.md).
using System;

namespace VRMR.Fbik
{
    public enum Allocator { Invalid = 0, None = 1, Temp = 2, TempJob = 3, Persistent = 4 }

    public struct NativeArray<T> : IDisposable where T : struct
    {
        internal T[] m_Buffer;
        public NativeArray(int length, Allocator allocator) { m_Buffer = new T[length]; }
        public NativeArray(T[] array, Allocator allocator) { m_Buffer = (T[])array.Clone(); }
        public NativeArray(NativeArray<T> array, Allocator allocator) { m_Buffer = (T[])array.m_Buffer.Clone(); }
        public bool IsCreated => m_Buffer != null;
        public int Length => m_Buffer == null ? 0 : m_Buffer.Length;
        public T this[int index]
        {
            get => m_Buffer[index];
            set => m_Buffer[index] = value;
        }
        /// <summary>Replacement for UnsafeUtility.ArrayElementAsRef(array.GetUnsafePtr(), index).</summary>
        public ref T ElementAt(int index) => ref m_Buffer[index];
        public void Dispose() { m_Buffer = null; }
        public void CopyTo(NativeArray<T> dst) { Array.Copy(m_Buffer, dst.m_Buffer, Length); }
        public void CopyTo(T[] dst) { Array.Copy(m_Buffer, dst, Length); }
        public void CopyFrom(NativeArray<T> src) { Array.Copy(src.m_Buffer, m_Buffer, Length); }
        public void CopyFrom(T[] src) { Array.Copy(src, m_Buffer, Length); }
        public T[] ToArray() => (T[])m_Buffer.Clone();
    }

    /// <summary>Common implementation for the FixedList*Bytes stand-ins.</summary>
    internal static class FixedListCore
    {
        public static void SetLength<T>(ref T[] buffer, ref int length, int value, int capacity)
        {
            if (value < 0 || value > capacity) throw new IndexOutOfRangeException($"Length {value} is out of range in FixedList of capacity {capacity}.");
            if (buffer == null) buffer = new T[Math.Max(value, 4)];
            else if (buffer.Length < value)
            {
                T[] grown = new T[Math.Max(value, buffer.Length * 2)];
                Array.Copy(buffer, grown, length);
                buffer = grown;
            }
            for (int i = value; i < length; i++) buffer[i] = default(T);
            length = value;
        }
        public static int Capacity(int bytes, int elementSize) => (bytes - 2) / Math.Max(1, elementSize);
    }

    public struct FixedList32Bytes<T> where T : unmanaged
    {
        T[] buffer; int length;
        public static unsafe int Capacity => FixedListCore.Capacity(32, sizeof(T));
        public int Length { get => length; set => FixedListCore.SetLength(ref buffer, ref length, value, Capacity); }
        public bool IsEmpty => length == 0;
        public T this[int index]
        {
            get { if ((uint)index >= (uint)length) throw new IndexOutOfRangeException(); return buffer[index]; }
            set { if ((uint)index >= (uint)length) throw new IndexOutOfRangeException(); buffer[index] = value; }
        }
        public void Add(in T item) { int i = length; Length = i + 1; buffer[i] = item; }
        public void Clear() { Length = 0; }
    }

    public struct FixedList64Bytes<T> where T : unmanaged
    {
        T[] buffer; int length;
        public static unsafe int Capacity => FixedListCore.Capacity(64, sizeof(T));
        public int Length { get => length; set => FixedListCore.SetLength(ref buffer, ref length, value, Capacity); }
        public bool IsEmpty => length == 0;
        public T this[int index]
        {
            get { if ((uint)index >= (uint)length) throw new IndexOutOfRangeException(); return buffer[index]; }
            set { if ((uint)index >= (uint)length) throw new IndexOutOfRangeException(); buffer[index] = value; }
        }
        public void Add(in T item) { int i = length; Length = i + 1; buffer[i] = item; }
        public void Clear() { Length = 0; }
    }

    public struct FixedList128Bytes<T> where T : unmanaged
    {
        T[] buffer; int length;
        public static unsafe int Capacity => FixedListCore.Capacity(128, sizeof(T));
        public int Length { get => length; set => FixedListCore.SetLength(ref buffer, ref length, value, Capacity); }
        public bool IsEmpty => length == 0;
        public T this[int index]
        {
            get { if ((uint)index >= (uint)length) throw new IndexOutOfRangeException(); return buffer[index]; }
            set { if ((uint)index >= (uint)length) throw new IndexOutOfRangeException(); buffer[index] = value; }
        }
        public void Add(in T item) { int i = length; Length = i + 1; buffer[i] = item; }
        public void Clear() { Length = 0; }
    }

    public struct FixedList512Bytes<T> where T : unmanaged
    {
        T[] buffer; int length;
        public static unsafe int Capacity => FixedListCore.Capacity(512, sizeof(T));
        public int Length { get => length; set => FixedListCore.SetLength(ref buffer, ref length, value, Capacity); }
        public bool IsEmpty => length == 0;
        public T this[int index]
        {
            get { if ((uint)index >= (uint)length) throw new IndexOutOfRangeException(); return buffer[index]; }
            set { if ((uint)index >= (uint)length) throw new IndexOutOfRangeException(); buffer[index] = value; }
        }
        public void Add(in T item) { int i = length; Length = i + 1; buffer[i] = item; }
        public void Clear() { Length = 0; }
    }
}
