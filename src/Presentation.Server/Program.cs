using ApplicationBuilderHelpers;
using Infrastructure.SignalR.Server;
using Infrastructure.SQLite;
using Presentation.Server;

var builder = ApplicationDependencyBuilder.FromBuilder(WebApplication.CreateBuilder(args));

builder.Add<PresentationServer>();
builder.Add<SignalRServerApplication>();
builder.Add<SQLiteApplication>();

builder.Run();
