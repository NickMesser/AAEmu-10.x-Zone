namespace AAEmu.Zone.Combat;

/// <summary>
/// Descending unsigned ordering used by the native aggro vectors (39325100/39324EA0).
/// Preserve its 32-entry insertion cutoff, median partition and equal-key swaps: tie order
/// affects subsequent target selection and which entry is evicted at capacity.
/// </summary>
internal static class ZoneAggroOrdering
{
    public static void Sort<T>(List<T> values, int first, int last, Func<T, uint> key) =>
        Sort(values, first, last, last - first, key);

    private static void Sort<T>(List<T> values, int first, int last, int ideal, Func<T, uint> key)
    {
        while (last - first > 32)
        {
            if (ideal < 1)
            {
                HeapSort(values, first, last, key);
                return;
            }
            var (left, right) = Partition(values, first, last, key);
            ideal = ideal / 2 + ideal / 2 / 2;
            if (left - first < last - right)
            {
                Sort(values, first, left, ideal, key);
                first = right;
            }
            else
            {
                Sort(values, right, last, ideal, key);
                last = left;
            }
        }
        for (var current = first + 1; current < last; current++)
        {
            var value = values[current];
            var hole = current;
            while (hole > first && key(value) > key(values[hole - 1]))
            {
                values[hole] = values[hole - 1];
                hole--;
            }
            values[hole] = value;
        }
    }

    private static (int Left, int Right) Partition<T>(List<T> values, int first, int last, Func<T, uint> key)
    {
        var mid = first + (last - first) / 2;
        // 39322F70 tests the distance to the final element, not the range length.
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
        while (first < pivotFirst && key(values[pivotFirst - 1]) == key(values[pivotFirst])) pivotFirst--;
        while (pivotLast < last && key(values[pivotLast]) == key(values[pivotFirst])) pivotLast++;
        var scanFirst = pivotLast;
        var scanLast = pivotFirst;
        while (true)
        {
            for (; scanFirst < last; scanFirst++)
            {
                if (key(values[pivotFirst]) > key(values[scanFirst])) continue;
                if (key(values[scanFirst]) > key(values[pivotFirst])) break;
                Swap(values, pivotLast++, scanFirst);
            }
            for (; first < scanLast; scanLast--)
            {
                if (key(values[scanLast - 1]) > key(values[pivotFirst])) continue;
                if (key(values[pivotFirst]) > key(values[scanLast - 1])) break;
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
            if (key(values[center]) > key(values[left])) Swap(values, left, center);
            if (key(values[right]) > key(values[center]))
            {
                Swap(values, center, right);
                if (key(values[center]) > key(values[left])) Swap(values, left, center);
            }
        }
    }

    private static void HeapSort<T>(List<T> values, int first, int last, Func<T, uint> key)
    {
        var count = last - first;
        for (var parent = count / 2; parent > 0;)
        {
            parent--;
            Adjust(parent, count, values[first + parent]);
        }
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
                if (key(values[first + child]) > key(values[first + child - 1])) child--;
                values[first + hole] = values[first + child];
                hole = child;
                child = child * 2 + 2;
            }
            if (child == length)
            {
                values[first + hole] = values[first + child - 1];
                hole = child - 1;
            }
            for (var parent = (hole - 1) / 2; hole > top && key(values[first + parent]) > key(value); parent = (hole - 1) / 2)
            {
                values[first + hole] = values[first + parent];
                hole = parent;
            }
            values[first + hole] = value;
        }
    }

    private static void Swap<T>(List<T> values, int left, int right) => (values[left], values[right]) = (values[right], values[left]);
}
