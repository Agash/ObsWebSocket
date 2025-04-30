using System.ComponentModel;

// This file is necessary for Projects targeting older frameworks like netstandard2.0
// which don't include IsExternalInit, a type required by the C# 9+ 'init' accessor used in records.
// See: https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/proposals/csharp-9.0/init
#pragma warning disable IDE0130 // Namespace does not match folder structure
namespace System.Runtime.CompilerServices;

#pragma warning restore IDE0130 // Namespace does not match folder structure

/// <summary>
/// Reserved to be used by the compiler for tracking metadata.
/// This class should not be used by developers in source code.
/// It allows the use of 'init' accessors in projects targeting older frameworks.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
// Note: The type must be internal or public, but static is convention.
internal static class IsExternalInit { }
