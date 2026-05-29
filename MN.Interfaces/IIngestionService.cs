using MN.Models;

namespace MN.Interfaces;

public interface IIngestionService
{
    Task<IngestEventResponse> IngestAsync(IngestEventRequest request, CancellationToken ct);
}
