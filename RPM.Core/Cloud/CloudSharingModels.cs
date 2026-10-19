using System.Text.Json.Serialization;

namespace RPM.Core.Cloud;

/// <summary>
/// One install of RPM that the account has signed in on, as the store lists them.
///
/// <para>This makes automatic sharing between a person's own computers possible. A machine cannot
/// vouch for itself, so the account holds the keys it has been signed in with, and a peer that
/// presents one of them is one of the person's own.</para>
/// </summary>
public sealed record CloudDevice(
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("publicKey")] string PublicKey,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("lastSeenAt")] long LastSeenAt = 0);

/// <summary>One account letting another hear its cloud-stored albums.</summary>
public sealed record CloudShare(
    [property: JsonPropertyName("ownerId")] string OwnerId,
    [property: JsonPropertyName("granteeId")] string GranteeId,
    [property: JsonPropertyName("granteeName")] string GranteeName,
    [property: JsonPropertyName("createdAt")] long CreatedAt = 0);

/// <summary>Both directions at once: who this account shares with, and who shares with it.</summary>
public sealed record CloudShares(
    [property: JsonPropertyName("granted")] IReadOnlyList<CloudShare> Granted,
    [property: JsonPropertyName("received")] IReadOnlyList<CloudShare> Received);
