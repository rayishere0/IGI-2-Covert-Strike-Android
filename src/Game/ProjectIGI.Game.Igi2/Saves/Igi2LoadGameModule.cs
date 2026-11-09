using ProjectIGI.Engine.Titles;
using ProjectIGI.Game.Player;

namespace ProjectIGI.Game.Igi2.Saves;

/// <summary>Publishes the main menu's load list: the saves the active profile keeps, across its missions.</summary>
/// <remarks>
/// The provider is built at registration from the configuration and the save session, both of which other modules already publish; the
/// file system it decodes briefing sprites through only exists at load time, so <see cref="LoadTables"/> hands it over then. A
/// thumbnail asked for before that simply has none.
/// </remarks>
public sealed class Igi2LoadGameModule : IContentModule
{
    /// <inheritdoc/>
    public string Name => "Load game";

    /// <inheritdoc/>
    public void Register(ContentRegistrationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Services.GetOrProvide<ITitleLoadGame>(() => new Igi2LoadGame(
            context.Services.GetOrProvide(() => new Config.Igi2GameConfig()),
            context.Services.GetOrProvide(static () => new Igi2SaveSession())));
    }

    /// <inheritdoc/>
    public void LoadTables(ContentLoadContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.Services.TryGet(out ITitleLoadGame? loads) && loads is Igi2LoadGame loadGame)
        {
            loadGame.FileSystem = context.FileSystem;
        }
    }
}
