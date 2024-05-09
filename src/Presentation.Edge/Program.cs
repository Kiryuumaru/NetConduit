using ApplicationBuilderHelpers;
using Infrastructure.SignalR.Edge;
using Infrastructure.SQLite;
using Microsoft.Extensions.Hosting;
using Presentation.Edge;

var builder = ApplicationDependencyBuilder.FromBuilder(Host.CreateApplicationBuilder(args));

builder.Add<PresentationEdge>();
builder.Add<SignalREdgeApplication>();
builder.Add<SQLiteApplication>();

builder.Run();
