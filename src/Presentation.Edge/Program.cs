using ApplicationBuilderHelpers;
using Infrastructure.SQLite;
using Microsoft.Extensions.Hosting;
using Presentation.Edge;

var builder = ApplicationDependencyBuilder.FromBuilder(Host.CreateApplicationBuilder(args));

builder.Add<PresentationEdge>();
builder.Add<SQLiteApplication>();

builder.Run();
