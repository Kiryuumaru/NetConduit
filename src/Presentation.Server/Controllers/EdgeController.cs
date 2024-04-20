using Application.Server.Edge.Services;
using Domain.Edge.Dtos;
using Domain.Edge.Entities;
using Microsoft.AspNetCore.Mvc;
using RestfulHelpers.Common;
using TransactionHelpers;

namespace Presentation.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
public class EdgeController(EdgeService edgeService) : ControllerBase
{
    private readonly EdgeService _edgeService = edgeService;

    [HttpGet]
    public Task<HttpResult<EdgeEntity[]>> Get()
    {
        return _edgeService.GetAll();
    }

    [HttpGet("{id}")]
    public Task<HttpResult<EdgeEntity>> Get(string id)
    {
        return _edgeService.Get(id);
    }

    [HttpPost]
    public Task<HttpResult<EdgeEntity>> Post([FromBody] EdgeAddDto edge)
    {
        return _edgeService.Create(edge);
    }

    [HttpPut("{id}")]
    public Task<HttpResult<EdgeEntity>> Put(string id, [FromBody] EdgeEditDto edge)
    {
        return _edgeService.Edit(id, edge);
    }

    [HttpDelete("{id}")]
    public Task<HttpResult> Delete(string id)
    {
        return _edgeService.Delete(id);
    }
}
