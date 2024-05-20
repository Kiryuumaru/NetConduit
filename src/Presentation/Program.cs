using ApplicationBuilderHelpers;
using Infrastructure.SignalR.Edge;
using Infrastructure.SQLite;
using Microsoft.Extensions.Hosting;
using Presentation;

var builder = ApplicationDependencyBuilder.FromBuilder(WebApplication.CreateBuilder(args));

builder.Add<BasePresentation>();
builder.Add<SignalRApplication>();
builder.Add<SQLiteApplication>();

builder.Run();
