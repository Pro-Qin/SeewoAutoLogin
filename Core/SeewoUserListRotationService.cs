using System;
using System.Collections.Generic;
using System.Linq;

namespace SeewoAutoLogin
{
    public sealed class SeewoUserListRotationService : IDisposable
    {
        private static readonly TimeSpan SameSessionWindow = TimeSpan.FromSeconds(10);
        private readonly object _sync = new object();
        private DateTimeOffset? _firstRequestAt;
        private int _groupIndex;
        private int _totalRequests;

        public event Action<int, bool> RotationChanged;

        public int CurrentGroupIndex
        {
            get { lock (_sync) return _groupIndex; }
        }

        public int TotalRequests
        {
            get { lock (_sync) return _totalRequests; }
        }

        public DateTimeOffset? LastRotationAt { get; private set; }

        public IReadOnlyList<SeewoAccount> SelectAccountsForRequest(IReadOnlyList<SeewoAccount> allAccounts, out int groupIndex, int configuredGroupSize = 6)
        {
            var snapshot = (allAccounts ?? Array.Empty<SeewoAccount>())
                .Where(account => account != null && !string.IsNullOrWhiteSpace(account.Username))
                .ToList();
            groupIndex = 0;
            if (snapshot.Count == 0) return snapshot;
            var groupSize = NormalizeGroupSize(configuredGroupSize);
            if (snapshot.Count <= groupSize)
            {
                RecordGroupIndex(0);
                groupIndex = 0;
                return snapshot;
            }

            int advanceTo;
            int indexAfterAdvance;
            bool withinWindow;
            lock (_sync)
            {
                _totalRequests++;
                if (_firstRequestAt.HasValue &&
                    DateTimeOffset.UtcNow - _firstRequestAt.Value <= SameSessionWindow)
                {
                    indexAfterAdvance = (_groupIndex + 1) % (int)Math.Ceiling(snapshot.Count / (double)groupSize);
                }
                else
                {
                    indexAfterAdvance = 0;
                }
                _groupIndex = indexAfterAdvance;
                _firstRequestAt = DateTimeOffset.UtcNow;
                advanceTo = _groupIndex;
                withinWindow = indexAfterAdvance != 0;
            }

            LastRotationAt = DateTimeOffset.UtcNow;
            groupIndex = advanceTo;
            RotationChanged?.Invoke(advanceTo, withinWindow);
            return snapshot.Skip(advanceTo * groupSize).Take(groupSize).ToList();
        }

        public static int NormalizeGroupSize(int groupSize) => groupSize == 4 ? 4 : 6;

        private void RecordGroupIndex(int index)
        {
            lock (_sync)
            {
                _groupIndex = index;
                _firstRequestAt = DateTimeOffset.UtcNow;
                _totalRequests++;
            }
            LastRotationAt = DateTimeOffset.UtcNow;
            RotationChanged?.Invoke(index, false);
        }

        public void Dispose() { }
    }
}
