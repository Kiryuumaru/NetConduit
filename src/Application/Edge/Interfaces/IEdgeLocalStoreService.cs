using Domain.Edge.Dtos;
using RestfulHelpers.Common;

namespace Application.Edge.Interfaces;

public interface IEdgeLocalStoreService
{
    Task<HttpResult<GetEdgeWithTokenDto>> Get(CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeWithTokenDto>> Create(AddEdgeDto edgeAddDto, CancellationToken cancellationToken = default);

    Task<HttpResult<bool>> Contains(CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeWithTokenDto>> GetOrCreate(Func<AddEdgeDto> onCreate, CancellationToken cancellationToken = default);
}
