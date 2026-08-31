# Contributing to ObsWebSocket.Core

First off, thank you for considering contributing to ObsWebSocket.Core! 🎉 Your help is appreciated, whether it's reporting bugs, suggesting features, or writing code.

This document provides guidelines for contributing to the project.

## How Can I Contribute?

*   **Reporting Bugs:** Found something not working as expected? Please open an issue!
*   **Suggesting Enhancements:** Have an idea for a new feature or an improvement? Open an issue to discuss it.
*   **Code Contributions:** Want to fix a bug or implement a feature? Submit a pull request!

## Reporting Bugs 🐞

Before creating a bug report, please check existing issues to see if someone else has already reported it.

When creating a bug report, please include as much detail as possible:

*   **A clear and descriptive title.**
*   **Steps to reproduce the behavior:** Be specific!
*   **Expected behavior:** What you thought would happen.
*   **Actual behavior:** What actually happened.
*   **Version information:**
    *   ObsWebSocket.Core version (or commit hash if building from source)
    *   OBS Studio version
    *   obs-websocket plugin version (if using OBS < 28, though this library targets v5+)
    *   .NET version (`dotnet --version`)
    *   Operating System
*   **Screenshots or logs:** If applicable, these can be very helpful.

[➡️ Open a New Bug Report](https://github.com/Agash/ObsWebSocket/issues/new?assignees=&labels=bug&template=bug_report.md&title=Bug:)

## Suggesting Enhancements 💡

We love new ideas! Feel free to suggest enhancements:

*   **Use a clear and descriptive title.**
*   **Explain the problem or motivation:** Why is this enhancement needed? What use case does it support?
*   **Describe the proposed solution:** How would this feature work?
*   **Consider alternatives:** Have you thought of other ways to achieve the same goal?

[➡️ Suggest an Enhancement](https://github.com/Agash/ObsWebSocket/issues/new?assignees=&labels=enhancement&template=feature_request.md&title=Feature:)

## Development Setup 🛠️

To contribute code, you'll need to set up a local development environment:

1.  **Prerequisites:**
    *   [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) (Latest Preview)
    *   [Git](https://git-scm.com/)
    *   An IDE like Visual Studio 2022 (Preview), VS Code with C# Dev Kit, or JetBrains Rider.
    *   OBS Studio (v28+) installed locally for testing (manual setup required for integration tests).
2.  **Clone the Repository:**
    ```bash
    git clone https://github.com/Agash/ObsWebSocket.git
    cd ObsWebSocket
    ```
3.  **Build the Solution:**
    ```bash
    dotnet build
    ```
    *(This will also run the source generators)*
4.  **Run Unit Tests:**
    ```bash
    dotnet test ObsWebSocket.Tests/ObsWebSocket.Tests.csproj
    ```
5.  **(Optional) Run Integration Tests:**
    *   These require a running OBS instance configured with specific scenes/sources (details TBD).
    *   Use the Test Explorer in your IDE or `dotnet test --filter TestCategory=Integration`.

## Pull Request Process 🚀

1.  **Fork the repository** and create your branch from `main`.
2.  **Make your changes.** Ensure code follows the project's style guidelines.
3.  **Add tests** for any new functionality or bug fixes.
4.  **Ensure all tests pass** (`dotnet test`).
5.  **Update documentation** (README.md, XML comments) if you added or changed APIs.
6.  **Commit your changes** using descriptive commit messages.
7.  **Push your branch** to your fork.
8.  **Open a pull request** back to the main repository. Explain your changes clearly in the PR description.

## Code Style ✨

*   Follow modern C# 13 / .NET 9 idioms (file-scoped namespaces, `using static`, pattern matching, etc.).
*   Adhere to the [.NET Runtime Coding Guidelines](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/coding-style.md) where applicable.
*   Use meaningful names for variables, methods, and classes.
*   Add XML documentation comments for all public APIs.
*   *(Consider adding a linter like CSharpier or EditorConfig settings)*

## Code of Conduct

This project expects participants to adhere to a standard code of conduct. Please ensure your interactions are respectful and constructive.

---

Thank you again for your interest in contributing!

## House rules

- **Warnings are errors.** `TreatWarningsAsErrors` is on. Fix the diagnostic rather than suppressing
  it; a `NoWarn` or `#pragma` needs a comment saying why the rule genuinely does not apply.
- **Nullable reference types are enabled** everywhere. No `!` without a reason.
- **All I/O is async**, with a `CancellationToken` accepted and propagated. No `.Result`,
  `.GetAwaiter().GetResult()`, or `Thread.Sleep`.
- **Public API carries XML documentation.**
- **The package is trim- and AOT-clean.** `IsAotCompatible` is set, so the trim and AOT analyzers run
  on every build. Serialization goes through a source-generated `JsonSerializerContext`, never the
  reflection-based `JsonSerializer` overloads.

## Tests

- Name tests `{Method}_{Scenario}_{ExpectedResult}`.
- Prefer the purpose-built MSTest assertions (`Assert.HasCount`, `Assert.Contains`,
  `Assert.AreSequenceEqual`) over hand-rolled equality checks; the analyzers will point you at them.
- No `Thread.Sleep`. Use `TaskCompletionSource`, channels, or a fake clock.
- New behaviour needs a test. Bug fixes need a test that fails before the fix.

## Commits and pull requests

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/en/v1.0.0/):

```
fix(webhooks): reject a signature computed over the decoded body
```

Keep the subject under 50 characters and in the imperative mood. Add a body only when the reason for
the change would not be obvious to the next reader. Explain *why*, not *what*.

One logical change per commit. Rebase rather than merge when updating a branch.

## Code of conduct

This project follows the [Contributor Covenant](CODE_OF_CONDUCT.md). By participating you are
expected to uphold it.

## Reporting security issues

Please do not open a public issue. See [SECURITY.md](SECURITY.md).
