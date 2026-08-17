namespace SimplArchive.Api.Imap;

/// <summary>
/// Live-connection bookkeeping for the IMAP listener (ADR 0618): one total count taken at accept and one
/// per-user count taken at successful authentication, so the caps in <see cref="ImapOptions"/> can refuse
/// the connection that would exceed them. Owned by <see cref="ImapServer"/>, shared by its sessions.
/// </summary>
internal sealed class ImapConnectionRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, int> _perUser = [];
    private int _total;

    public bool TryAddConnection(int maxTotal)
    {
        lock (_gate)
        {
            if (_total >= maxTotal)
            {
                return false;
            }

            _total++;
            return true;
        }
    }

    public void RemoveConnection()
    {
        lock (_gate)
        {
            _total--;
        }
    }

    public bool TryAddUser(Guid userId, int maxPerUser)
    {
        lock (_gate)
        {
            var current = _perUser.GetValueOrDefault(userId);
            if (current >= maxPerUser)
            {
                return false;
            }

            _perUser[userId] = current + 1;
            return true;
        }
    }

    public void RemoveUser(Guid userId)
    {
        lock (_gate)
        {
            if (_perUser.TryGetValue(userId, out var current))
            {
                if (current <= 1)
                {
                    _perUser.Remove(userId);
                }
                else
                {
                    _perUser[userId] = current - 1;
                }
            }
        }
    }
}
