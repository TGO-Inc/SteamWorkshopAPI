using Newtonsoft.Json;
using System.Collections;
using System.Collections.Concurrent;
using System.Threading;

namespace SteamWorkshop.WebAPI.Internal
{
    internal class ManagedArray<T>(int length, bool threaded = false) : IEnumerable<T>
    {
        public readonly int Size = length;
        private readonly T[] base_array = new T[length];
        private readonly ConcurrentBag<T> bag = new();
        private readonly bool isThreaded = threaded;
        public int Count = 0;

        public T this[int index]
        {
            get => base_array[index];
            set => base_array[index] = value;
        }
        public void Add(T item)
        {
            if (Count >= Size)
                throw new ArrayFullException("T");

            if (isThreaded)
                bag.Add(item);
            else
                base_array[Count] = item;

            Interlocked.Increment(ref Count);
        }
        public void Add(T[] items)
        {
            if (Count + items.Length > Size)
                throw new ArrayFullException("T[]");

            if (isThreaded)
                foreach (T item in items)
                    bag.Add(item);
            else
                items.CopyTo(base_array, Count);

            Interlocked.Add(ref Count, items.Length);
        }
        public void Add(List<T> items)
        {

            if (Count + items.Count > Size)
                throw new ArrayFullException("List<T>");
            
            if (isThreaded)
                foreach (T item in items)
                    bag.Add(item);
            else
                items.CopyTo(base_array, Count);

            Interlocked.Add(ref Count, items.Count);

        }
        public void Add(IEnumerable<T> items)
        {
            int length = items.Count();
            if (Count + length > Size)
                throw new ArrayFullException("IEnumerable<T>");

            foreach (var item in items)
            {
                if (isThreaded)
                    bag.Add(item);
                else
                    this.Add(item);
            }

            Interlocked.Add(ref Count, length);
        }
        public IEnumerator<T> GetEnumerator()
        {
            if (isThreaded)
                foreach(var item in bag)
                    yield return item;
            else
                for (int i = 0; i < Count; i++)
                    yield return base_array[i];
        }
        IEnumerator IEnumerable.GetEnumerator()
        {
            if(isThreaded)
                return bag.GetEnumerator();
            else
                return this.GetEnumerator();
        }
        public T[] ToArray()
        {
            if (isThreaded)
                return this.bag.ToArray();
            else
                return this.base_array;
        }
    }
    internal class ArrayFullException : Exception
    {
        public ArrayFullException(string msg = "")
            : this("The array has reached capacity", msg)
        {
        }
        public ArrayFullException(string msg, bool t)
            : base(msg)
        {
        }
        public ArrayFullException(string msg, string inr)
            : base(msg, new ArrayFullException(inr, true))
        {
        }
    }
}
