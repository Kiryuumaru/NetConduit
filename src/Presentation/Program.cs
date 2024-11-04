using CliFx;

return await new CliApplicationBuilder()
    .SetExecutableName("netc")
    .AddCommandsFromThisAssembly()
    .Build()
    .RunAsync();
