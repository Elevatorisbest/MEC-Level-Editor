using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace Frosty.Core.Viewport
{
    /// <summary>
    /// Counters for how the Mirror's Edge Catalyst fallback shader set up materials (diagnostics
    /// only; read and reset by the level editor's load report).
    /// </summary>
    public static class MecFallbackStats
    {
        private static readonly ConcurrentDictionary<string, int> counts = new ConcurrentDictionary<string, int>();

        public static void Count(string key)
        {
            counts.AddOrUpdate(key, 1, (_, v) => v + 1);
        }

        public static List<KeyValuePair<string, int>> Snapshot()
        {
            return counts.OrderByDescending(kv => kv.Value).ToList();
        }

        public static void Reset()
        {
            counts.Clear();
        }
    }
}
