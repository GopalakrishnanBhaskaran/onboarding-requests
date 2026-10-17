using System.Collections.Concurrent;
using PmoReview.Core.Directory;
using PmoReview.Core.Domain;
using PmoReview.Core.Storage;
using PmoReview.Core.Workflow;

namespace PmoReview.Tests.Support;

/// <summary>In-memory, read-only Entra for tests. Tests arrange what the ID team / user did; the agent only reads.</summary>
public sealed class FakeDirectory : IDirectoryReader
{
    private readonly ConcurrentDictionary<string, DirectoryUser> _users = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<DirectoryGroup>> _groups = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HashSet<string>> _members = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (List<string> Types, TapMethod? Tap)> _methods = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _signIns = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _managers = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Next N reads throw this category (simulates Graph outages).</summary>
    public string? FailWith { get; set; }
    public int Reads { get; private set; }

    public static FakeDirectory SeededFromSample()
    {
        var d = new FakeDirectory();
        foreach (var g in new[]
                 {
                     "SAMPLE-GRP-ALPHA-DBW-ENG-USER", "SAMPLE-GRP-ALPHA-DBW-ENG-ADMIN", "SAMPLE-GRP-ALPHA-SALES-READ", "SAMPLE-GRP-ALPHA-SALES-WRITE",
                     "SAMPLE-GRP-ALPHA-LEGACY-USER", "SAMPLE-GRP-ALPHA-DRIVE-EDIT", "SAMPLE-GRP-ALPHA-DRIVE-EDIT-ALT", "sample_wiki_editors",
                     "SAMPLE-GRP-ALPHA-BILLING-ADMIN", "SAMPLE-GRP-BETA-TEST-USER",
                 })
            d.AddGroup(g);
        return d;
    }

    public DirectoryUser AddUser(string upn, bool guest = false)
    {
        var u = new DirectoryUser(Guid.NewGuid().ToString("D"), upn, guest ? "Guest" : "Member", true);
        _users[upn] = u;
        _methods[upn] = (["password"], null);
        return u;
    }

    public DirectoryGroup AddGroup(string name, bool security = true)
    {
        var g = new DirectoryGroup(Guid.NewGuid().ToString("D"), name, security);
        _groups.AddOrUpdate(name, [g], (_, list) => [.. list, g]);
        return g;
    }

    public void RemoveGroup(string name) => _groups.TryRemove(name, out _);

    public string GroupId(string name) => _groups[name][0].ObjectId;

    public void AddMember(string group, string upn) =>
        _members.AddOrUpdate(group, [upn], (_, set) => { set.Add(upn); return set; });

    public void RemoveMember(string group, string upn)
    {
        if (_members.TryGetValue(group, out var set)) set.Remove(upn);
    }

    public void SetMethods(string upn, IReadOnlyList<string> types) => _methods[upn] = ([.. types], _methods.TryGetValue(upn, out var m) ? m.Tap : null);

    public void SetTap(string upn, TapMethod? tap)
    {
        var current = _methods.TryGetValue(upn, out var m) ? m : (["password"], null);
        var types = current.Types.Where(t => t != "temporaryAccessPass").ToList();
        if (tap is not null) types.Add("temporaryAccessPass");
        _methods[upn] = (types, tap);
    }

    public void SignInNow(string upn, DateTimeOffset at) => _signIns[upn] = at;

    public void SetManager(string upn, string manager) => _managers[upn] = manager;

    private void Read()
    {
        Reads++;
        if (FailWith is { } f) throw new DirectoryReadException(f);
    }

    public Task<DirectoryUser?> FindUserAsync(string userPrincipalName, CancellationToken ct)
    {
        Read();
        return Task.FromResult(_users.TryGetValue(userPrincipalName, out var u) ? u : null);
    }

    public Task<IReadOnlyList<DirectoryGroup>> FindGroupsAsync(string displayName, CancellationToken ct)
    {
        Read();
        return Task.FromResult<IReadOnlyList<DirectoryGroup>>(_groups.TryGetValue(displayName, out var g) ? [.. g] : []);
    }

    public Task<bool> IsMemberAsync(string groupObjectId, string userObjectId, CancellationToken ct)
    {
        Read();
        var group = _groups.FirstOrDefault(g => g.Value.Any(x => x.ObjectId == groupObjectId)).Key;
        var user = _users.Values.FirstOrDefault(u => u.ObjectId == userObjectId)?.UserPrincipalName;
        return Task.FromResult(group is not null && user is not null && _members.TryGetValue(group, out var set) && set.Contains(user));
    }

    public Task<AuthMethods> GetAuthMethodsAsync(string userObjectId, CancellationToken ct)
    {
        Read();
        var upn = _users.Values.First(u => u.ObjectId == userObjectId).UserPrincipalName;
        var m = _methods.TryGetValue(upn, out var x) ? x : (["password"], null);
        return Task.FromResult(new AuthMethods(m.Types, m.Tap));
    }

    public Task<SignInSummary?> LastSuccessfulSignInAsync(string userObjectId, DateTimeOffset since, CancellationToken ct)
    {
        Read();
        var upn = _users.Values.First(u => u.ObjectId == userObjectId).UserPrincipalName;
        return Task.FromResult(_signIns.TryGetValue(upn, out var at) && at >= since ? new SignInSummary(at, true) : null);
    }

    public Task<string?> GetManagerAsync(string userPrincipalName, CancellationToken ct)
    {
        Read();
        return Task.FromResult(_managers.TryGetValue(userPrincipalName, out var m) ? m : null);
    }
}

/// <summary>Role assignments per (group object id, scope). Null scope type = not readable.</summary>
public sealed class FakeRoles : IRoleAssignmentReader
{
    private readonly ConcurrentDictionary<string, List<AssignedRole>> _assignments = new();
    public bool Unreadable { get; set; }

    public void Assign(string groupObjectId, string role, string? scope) =>
        _assignments.AddOrUpdate($"{groupObjectId}|{scope}", [new AssignedRole(role, scope)], (_, l) => [.. l, new AssignedRole(role, scope)]);

    public Task<IReadOnlyList<AssignedRole>?> ListForPrincipalAsync(string principalObjectId, string? scope, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<AssignedRole>?>(Unreadable ? null : _assignments.TryGetValue($"{principalObjectId}|{scope}", out var l) ? [.. l] : []);
}

/// <summary>Azure DevOps stand-in: records drafts, one work item per request id.</summary>
public sealed class FakeTracker : IWorkItemTracker
{
    public bool Enabled { get; set; } = true;
    public string? FailWith { get; set; }
    public List<WorkItemDraft> Drafts { get; } = [];
    private readonly Dictionary<string, int> _ids = [];

    public Task<AdoLink> SyncAsync(WorkItemDraft draft, AdoLink? existing, CancellationToken ct)
    {
        if (FailWith is { } f) throw new OutboundException(f);
        Drafts.Add(draft);
        if (!_ids.TryGetValue(draft.Tag, out var id)) _ids[draft.Tag] = id = 1000 + _ids.Count;
        return Task.FromResult(new AdoLink { WorkItemId = id, Url = $"https://dev.azure.invalid/sample/_workitems/edit/{id}" });
    }
}

/// <summary>A store whose writes and reads fail on demand: an error the agent cannot classify (e.g. a document it cannot read).</summary>
public sealed class FaultyStore<T>(IDocumentStore<T> inner) : IDocumentStore<T> where T : class, IDocument
{
    public bool FailWrites { get; set; }
    public bool FailLists { get; set; }

    public Task<Versioned<T>?> GetAsync(string partitionKey, string id, CancellationToken ct = default) => inner.GetAsync(partitionKey, id, ct);
    public Task<IReadOnlyList<Versioned<T>>> ListAsync(string partitionKey, CancellationToken ct = default) =>
        FailLists ? throw new InvalidOperationException("SAMPLE fault: list") : inner.ListAsync(partitionKey, ct);
    public Task<IReadOnlyList<Versioned<T>>> ListAllAsync(CancellationToken ct = default) => inner.ListAllAsync(ct);
    public Task<Versioned<T>> CreateAsync(T document, CancellationToken ct = default) =>
        FailWrites ? throw new InvalidOperationException("SAMPLE fault: write") : inner.CreateAsync(document, ct);
    public Task<Versioned<T>> ReplaceAsync(T document, string ifMatchETag, CancellationToken ct = default) => inner.ReplaceAsync(document, ifMatchETag, ct);
}
