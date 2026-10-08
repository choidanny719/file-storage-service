using FileStorage.Core;

namespace FileStorage.Api;

internal sealed record StoredUpload(Guid Id, Guid FileId, UploadRequest Request, string State,
    DateTimeOffset ExpiresAt, bool Expired, Guid? VersionId)
{
    public void RequirePending()
    {
        if (State != "pending") throw ApiFault.Conflict("Upload is no longer pending.");
        if (Expired) throw new ApiFault(410, "upload_expired", "Upload session expired.");
    }
}
