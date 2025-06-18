using System.Collections;
using System.Collections.Concurrent;
using JetBrains.Annotations;

namespace SteamWorkshop.WebAPI.Internal;

[PublicAPI]
internal class ManagedArray<T>(int length, bool threaded = false) : IEnumerable<T>
{
    public readonly int Size = length;
    private readonly T[] _baseArray = new T[length];
    private readonly ConcurrentBag<T> _bag = [];
    private readonly bool _isThreaded = threaded;
    public int Count = 0;

    public T this[int index]
    {
        get => this._baseArray[index];
        set => this._baseArray[index] = value;
    }
    public void Add(T item)
    {
        if (this.Count >= this.Size)
            throw new ArrayFullException("T");

        if (this._isThreaded)
            this._bag.Add(item);
        else
            this._baseArray[this.Count] = item;

        Interlocked.Increment(ref this.Count);
    }
    public void Add(T[] items)
    {
        if (this.Count + items.Length > this.Size)
            throw new ArrayFullException("T[]");

        if (this._isThreaded)
            foreach (T item in items)
                this._bag.Add(item);
        else
            items.CopyTo(this._baseArray, this.Count);

        Interlocked.Add(ref this.Count, items.Length);
    }
    public void Add(List<T> items)
    {

        if (this.Count + items.Count > this.Size)
            throw new ArrayFullException("List<T>");
            
        if (this._isThreaded)
            foreach (T item in items)
                this._bag.Add(item);
        else
            items.CopyTo(this._baseArray, this.Count);

        Interlocked.Add(ref this.Count, items.Count);

    }
    public void Add(IEnumerable<T> items)
    {
        var itemsArray = items as T[] ?? items.ToArray();
        int length = itemsArray.Length;
        if (this.Count + length > this.Size)
            throw new ArrayFullException("IEnumerable<T>");

        foreach (T item in itemsArray)
        {
            if (this._isThreaded)
                this._bag.Add(item);
            else
                this.Add(item);
        }

        Interlocked.Add(ref this.Count, length);
    }
    public IEnumerator<T> GetEnumerator()
    {
        if (this._isThreaded)
            foreach(T item in this._bag)
                yield return item;
        else
            for (var i = 0; i < this.Count; i++)
                yield return this._baseArray[i];
    }
    IEnumerator IEnumerable.GetEnumerator()
        => this._isThreaded ? this._bag.GetEnumerator() : this.GetEnumerator();

    public T[] ToArray()
        => this._isThreaded ? this._bag.ToArray() : this._baseArray;
}

[PublicAPI]
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