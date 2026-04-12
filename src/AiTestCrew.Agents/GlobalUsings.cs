// Resolve ambiguity between FlaUI.Core.Application and System.Windows.Forms.Application
// All desktop agent code uses FlaUI's Application; WinForms is only needed for Clipboard access.
global using Application = FlaUI.Core.Application;
