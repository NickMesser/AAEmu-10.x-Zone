namespace AAEmu.Zone.Simulation;

/// <summary>399CA750 and its pointer-vector sort helpers, including comparator call order.</summary>
internal static class ZoneNpcSpawnerOrdering
{
    internal static void Sort<T>(List<T> values, Func<T, T, bool> less) => Sort(values, 0, values.Count, values.Count, less);

    private static void Sort<T>(List<T> values, int first, int last, int ideal, Func<T, T, bool> less)
    {
        while (last - first > 32)
        {
            if (ideal < 1) { HeapSort(values, first, last, less); return; }
            var (left, right) = Partition(values, first, last, less);
            ideal = ideal / 2 + ideal / 2 / 2;
            if (left - first < last - right) { Sort(values, first, left, ideal, less); first = right; }
            else { Sort(values, right, last, ideal, less); last = left; }
        }
        //399CA2B0 first tests the range's initial entry, then its predecessor, even when equal.
        for (var at = first + 1; at < last; at++)
        {
            var value = values[at];
            var hole = at;
            if (less(value, values[first]))
            {
                while (hole > first) { values[hole] = values[hole - 1]; hole--; }
            }
            else
            {
                // The bound also prevents native out-of-bounds behavior if a scene supplies a
                // changing camera that invalidates the ordering between successive comparisons.
                while (hole > first && less(value, values[hole - 1])) { values[hole] = values[hole - 1]; hole--; }
            }
            values[hole] = value;
        }
    }

    private static (int Left, int Right) Partition<T>(List<T> values, int first, int last, Func<T, T, bool> less)
    {
        var mid = first + (last - first) / 2;
        if (last - 1 - first > 40)
        {
            var step = (last - first) / 8;
            Median(first, first + step, first + 2 * step);
            Median(mid - step, mid, mid + step);
            Median(last - 1 - 2 * step, last - 1 - step, last - 1);
            Median(first + step, mid, last - 1 - step);
        }
        else Median(first, mid, last - 1);
        var pivotFirst = mid;
        var pivotLast = mid + 1;
        while (first < pivotFirst && !less(values[pivotFirst - 1], values[pivotFirst]) && !less(values[pivotFirst], values[pivotFirst - 1])) pivotFirst--;
        while (pivotLast < last && !less(values[pivotLast], values[pivotFirst]) && !less(values[pivotFirst], values[pivotLast])) pivotLast++;
        var scanFirst = pivotLast;
        var scanLast = pivotFirst;
        while (true)
        {
            for (; scanFirst < last; scanFirst++)
            {
                if (less(values[pivotFirst], values[scanFirst])) continue;
                if (less(values[scanFirst], values[pivotFirst])) break;
                Swap(values, pivotLast++, scanFirst);
            }
            for (; first < scanLast; scanLast--)
            {
                if (less(values[scanLast - 1], values[pivotFirst])) continue;
                if (less(values[pivotFirst], values[scanLast - 1])) break;
                Swap(values, --pivotFirst, scanLast - 1);
            }
            if (scanLast == first && scanFirst == last) return (pivotFirst, pivotLast);
            if (scanLast == first)
            {
                if (pivotLast != scanFirst) Swap(values, pivotFirst, pivotLast);
                pivotLast++;
                Swap(values, pivotFirst++, scanFirst++);
            }
            else if (scanFirst == last)
            {
                if (--scanLast != --pivotFirst) Swap(values, scanLast, pivotFirst);
                Swap(values, pivotFirst, --pivotLast);
            }
            else Swap(values, scanFirst++, --scanLast);
        }

        void Median(int left, int center, int right)
        {
            if (less(values[center], values[left])) Swap(values, left, center);
            if (less(values[right], values[center])) Swap(values, center, right);
            if (less(values[center], values[left])) Swap(values, left, center);
        }
    }

    private static void HeapSort<T>(List<T> values, int first, int last, Func<T, T, bool> less)
    {
        var count = last - first;
        for (var parent = count / 2; parent > 0;) { parent--; Adjust(parent, count, values[first + parent]); }
        while (count > 1)
        {
            var value = values[first + --count];
            values[first + count] = values[first];
            Adjust(0, count, value);
        }
        void Adjust(int hole, int length, T value)
        {
            var top = hole;
            var child = hole * 2 + 2;
            while (child < length)
            {
                if (less(values[first + child], values[first + child - 1])) child--;
                values[first + hole] = values[first + child]; hole = child; child = child * 2 + 2;
            }
            if (child == length) { values[first + hole] = values[first + child - 1]; hole = child - 1; }
            for (var parent = (hole - 1) / 2; hole > top && less(values[first + parent], value); parent = (hole - 1) / 2)
            { values[first + hole] = values[first + parent]; hole = parent; }
            values[first + hole] = value;
        }
    }
    private static void Swap<T>(List<T> values, int left, int right) => (values[left], values[right]) = (values[right], values[left]);
}
