<project_philosophy>
Focus: Windows-native .NET 8.0 WPF desktop client with hierarchical 3-stage memory pipeline, non-blocking System.Threading.Channels streaming, and O(1) file persistence for Gemini roleplay.
</project_philosophy>

<engineering_rules>
- **Architecture**: Strict CommunityToolkit.Mvvm pattern. Decouple ViewModel from View. Keep UI responsive via asynchronous event pipelines.
- **Concurrency/Resource**: Enforce non-blocking UI thread (STA); offload Gemini API streaming and file I/O to background tasks via System.Threading.Channels.
- **Formatting**: Zero decorative emojis. Strict technical markdown.
</engineering_rules>

<critical_rules>
- **Build**: `dotnet build GRC.sln`
- **Test**: `dotnet test GRC.sln --filter "Category!=Integration"` (외부 API 연동 시험: `dotnet test GRC.sln --filter "Category=Integration"`)
- **Release**: `powershell -ExecutionPolicy Bypass -File .\build_release.ps1`
- **Invariants**: NEVER perform heavy serialization/deserialization or blocking network I/O on the UI thread; use async streams and binary seek log append.
- **Paths**: Use relative paths exclusively (`../Obsidian.Agent/`, etc.).
</critical_rules>

<context_triggers>
- **Architecture**: Memory pipeline, channels, O(1) logger, audio prefetch -> `../Obsidian.Agent/GRC/docs/01_grc_architecture.md`
- **WPF Standards**: Modern MVVM, async commands, DWM styling -> `../Obsidian.Agent/ai_agent_refs/WPF_Architecture_Guidelines.md`
- **Prompt System**: Gemini system instructions, roleplay prompt schema -> `../Obsidian.Agent/ai_agent_refs/AI_Prompt_Engineering_Guidelines.md`
- **Troubleshooting**: WPF STA threading, Gemini streaming, logger traps -> `../Obsidian.Agent/troubleshooting/grc.md`
</context_triggers>

<post_action>
- **Log**: Document edge cases and crashes in `../Obsidian.Agent/troubleshooting/grc.md`.
- **Sync**: Update specifications in `../Obsidian.Agent/GRC/docs/` if architecture changes.
- **Audit**: Verify all relative links resolve to physical files on disk.
</post_action>
