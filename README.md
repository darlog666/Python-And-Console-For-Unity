# Unity Advanced Terminal

A custom terminal window for the Unity Editor. It combines a native OS shell, an isolated Python execution environment, and Unity's debug logs into a single UIElements-based interface.

Built for developers who want to run Git commands, automate build pipelines, or test Python scripts without alt-tabbing out of the editor.

## Features

- **Shell Fallback**: Type `git status` or `npm install` directly. Unrecognized commands are automatically routed to the host OS shell (cmd/bash).
- **Python Bridge**: Execute Python scripts with full `stdout`/`stderr` capture. Uses a Base64/JSON IPC protocol to prevent parsing errors with quotes and newlines.
- **Built-in IDE**: A code editor with syntax highlighting, PEP8 checks, and auto-closing brackets.
- **Log Mirror**: Streams Unity's native `Debug.Log`, warnings, and exceptions into the terminal in real-time.
- **Macros**: Bind `.py` scripts to toolbar buttons for one-click automation.
- **Autocomplete**: Context-aware suggestions for internal commands, local files, and Unity API methods.

## Installation

1. Drop the `AdvancedTerminal` folder into your project's `Assets/Editor/` directory.
2. Make sure Python 3 is installed and available in your system's `PATH`.
3. Open the window via `Window > Advanced Terminal` (Shortcut: `Ctrl + Shift + ~`).

## Usage

### Terminal Input

| Input | Action |
| :--- | :--- |
| `git pull origin main` | Runs in the native OS shell (fallback). |
| `py print("Hello Unity")` | Executes a Python expression. |
| `help` | Lists all internal terminal commands. |
| `clear` / `cls` | Clears the console buffer. |
| `=env=prod` | Binds a quick session variable. |

### Python IDE (Right Panel)

- **Ctrl + Enter**: Run the current code buffer.
- **Ctrl + S**: Save the script to `Assets/Editor/PythonScripts/`.
- **Tab**: Inserts 4 spaces.
- **Format Code**: Runs a basic PEP8 audit.

## Configuration

If the terminal fails to locate your Python interpreter, set a system environment variable:

| Variable | Description |
| :--- | :--- |
| `ADVTERM_PYTHON_PATH` | Absolute path to your Python executable (e.g., `C:\Python39\python.exe`). |

## Under the Hood

- **UI**: Built with UIElements (UXML + USS). Theming via CSS variables in `TerminalStyles.uss`.
- **IPC Protocol**: C# to Python communication uses an async TCP socket and a Base64-wrapped JSON protocol. This avoids parsing breaks caused by quotes, newlines, or special characters.
- **Threading**: OS process streams run asynchronously. Output is marshaled to the Main Thread via a custom `EditorMainThreadDispatcher` to prevent editor freezing.
- **Memory**: Ring buffer for logs with automatic pruning (100k entries ceiling) to prevent memory leaks during long sessions.

## Known Issues

- The Python environment is isolated per execution. If you need persistent state across `py` commands, use the IDE or save your script to a file.
- If a native command expects interactive input (like an SSH password prompt), it will hang until the 60-second timeout.

## License

MIT.