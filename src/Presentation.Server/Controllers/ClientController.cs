using Application.Server.Client.Services;
using Domain.Client.Dtos;
using Domain.Client.Entities;
using Microsoft.AspNetCore.Mvc;
using RestfulHelpers.Common;
using TransactionHelpers;

namespace Presentation.Server.Controllers;

[Route("api/[controller]")]
[ApiController]
public class ClientController(ClientService clientService) : ControllerBase
{
    private readonly ClientService _clientService = clientService;

    [HttpGet]
    public Task<HttpResult<ClientEntity[]>> Get()
    {
        return _clientService.GetAll();
    }

    [HttpGet("{id}")]
    public Task<HttpResult<ClientEntity>> Get(string id)
    {
        return _clientService.Get(id);
    }

    [HttpPost]
    public Task<HttpResult<ClientEntity>> Post([FromBody] ClientAddDto client)
    {
        return _clientService.Create(client);
    }

    [HttpPut("{id}")]
    public Task<HttpResult<ClientEntity>> Put(string id, [FromBody] ClientEditDto client)
    {
        return _clientService.Edit(id, client);
    }

    [HttpDelete("{id}")]
    public Task<HttpResult> Delete(string id)
    {
        return _clientService.Delete(id);
    }
}
