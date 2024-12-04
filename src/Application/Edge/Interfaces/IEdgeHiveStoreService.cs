using Domain.Edge.Dtos;
using RestfulHelpers.Common;

namespace Application.Edge.Interfaces;

public interface IEdgeHiveStoreService
{
    Task<HttpResult<bool>> Contains(string id, CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeInfoDto[]>> GetAll(CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeInfoDto>> Get(string id, CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeWithTokenDto>> GetToken(string id, CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeWithTokenDto>> Create(AddEdgeDto edgeAddDto, CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeWithTokenDto>> Edit(string id, EditEdgeDto edgeEditDto, CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeInfoDto>> Delete(string id, CancellationToken cancellationToken = default);

    Task<HttpResult<GetEdgeWithTokenDto>> GetOrCreate(string id, Func<AddEdgeDto> onCreate, CancellationToken cancellationToken = default);
}
