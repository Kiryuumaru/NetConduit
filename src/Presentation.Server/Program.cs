using ApplicationBuilderHelpers;
using Infrastructure.SQLite;
using Presentation.Server;

var builder = ApplicationDependencyBuilder.FromBuilder(WebApplication.CreateBuilder(args));

builder.Add<PresentationServer>();
builder.Add<SQLiteApplication>();

builder.Run();
