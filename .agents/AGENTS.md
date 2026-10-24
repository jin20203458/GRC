<project_philosophy>
Focus: .NET 8.0 WPF client with Cognitive Core 3.0 (Prefix-Frozen SystemInstruction, 768-dim Single Event Ledger, Bi-Temporal Entity Projection, Hybrid Lorebook & Gated Salience RAG), Channels streaming, and O(1) persistence for Gemini roleplay.
</project_philosophy>

<engineering_rules>
- **Architecture**: Strict CommunityToolkit.Mvvm. Decouple ViewModels from Views via `IDialogService`; style UI with 3-tier XAML tokens (`PrimitiveTokens`, `DarkTheme`, `ModernStyles`).
- **Concurrency/Resource**: Stream via `System.Threading.Channels` with 60FPS `DispatcherTimer` batching; persist logs in O(1) via `FullHistoryLogger` binary seek append.
- **Formatting**: Strictly follow target file style and indentation. Technical markdown; zero decorative emojis.
</engineering_rules>

<critical_rules>
- **Build**: `dotnet build GRC.sln -nodeReuse:false`
- **Test**:
  - Fast QA: `dotnet test tests/GRC.Tests/GRC.Tests.csproj --filter "Category!=Integration" --no-build -v n`
  - Warning: NEVER omit filter during standard QA (unfiltered runs trigger live Vertex AI calls and billing).
  - Full-Chain: `dotnet test tests/GRC.Tests/GRC.Tests.csproj --filter "Category=Integration" -v n`
- **Release**: `powershell -ExecutionPolicy Bypass -File .\build_release.ps1`
- **Invariants**:
  - NEVER run chained build and test commands in a single background line; execute test commands directly targeting the test project (`tests/GRC.Tests/GRC.Tests.csproj`) with `-v n` and `-nodeReuse:false` to prevent MSBuild/VSTest child pipe deadlocks.
  - NEVER block the STA UI thread or call unguarded `Application.Current.Dispatcher` in ViewModels/services; use async pipelines and captured `Dispatcher` guards.
  - NEVER inject dynamic context into `SystemInstruction`; keep it prefix-frozen and pack lorebooks/ledgers/states into the latest user turn via `CognitivePromptAssembler`.
  - NEVER unify Vertex AI regions or swap Korean embedding models; preserve strict LLM/Embedding/TTS endpoint split in `GeminiApiService`.
- **Paths**: Use relative paths exclusively (`../Obsidian.Agent/`, etc.).
</critical_rules>

<context_triggers>
- **Architecture**: Cognitive Core 3.0, EventLedger, HybridLorebook, GatedRAG, Channels, O(1) logger, SessionArchitect, Design tokens, SmartAutoScroll -> `../Obsidian.Agent/GRC/docs/01_grc_architecture.md`
- **Troubleshooting**: STA threading, Vertex AI regions, LlmJsonParser, TensorPrimitives NaN, AutoScrollHelper, race conditions -> `../Obsidian.Agent/troubleshooting/grc.md`
</context_triggers>

<post_action>
- **Log**: Document resolved bugs, edge cases, and crashes in `../Obsidian.Agent/troubleshooting/grc.md`.
- **Sync**: Update specifications in `../Obsidian.Agent/GRC/docs/` if architecture changes, and verify all relative links resolve to physical files on disk.
</post_action>

