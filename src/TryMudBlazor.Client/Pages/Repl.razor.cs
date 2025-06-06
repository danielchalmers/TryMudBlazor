﻿namespace TryMudBlazor.Client.Pages
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Threading.Tasks;
    using Microsoft.AspNetCore.Components;
    using Microsoft.CodeAnalysis;
    using Microsoft.JSInterop;
    using MudBlazor;
    using Try.Core;
    using TryMudBlazor.Client.Components;
    using TryMudBlazor.Client.Models;
    using TryMudBlazor.Client.Services;

    public partial class Repl : IDisposable
    {
        [Inject] private LayoutService LayoutService { get; set; }

        private const string MainComponentCodePrefix = "@page \"/__main\"\n";
        private const string MainUserPagePath = "/__main";

        private DotNetObjectReference<Repl> dotNetInstance;
        private string errorMessage;
        private CodeFile activeCodeFile;

        [Inject]
        public ISnackbar Snackbar { get; set; }

        [Inject]
        public SnippetsService SnippetsService { get; set; }

        [Inject]
        public CompilationService CompilationService { get; set; }

        [Inject]
        public IJSInProcessRuntime JsRuntime { get; set; }

        [Inject]
        public NavigationManager NavigationManager { get; set; }

        [Parameter]
        public string SnippetId { get; set; }

        public CodeEditor CodeEditorComponent { get; set; }

        public IDictionary<string, CodeFile> CodeFiles { get; set; } = new Dictionary<string, CodeFile>();

        private IList<string> CodeFileNames { get; set; } = new List<string>();

        private string CodeEditorContent => this.activeCodeFile?.Content;

        private CodeFileType CodeFileType => this.activeCodeFile?.Type ?? CodeFileType.Razor;

        private bool SaveSnippetPopupVisible { get; set; }

        private IReadOnlyCollection<CompilationDiagnostic> Diagnostics { get; set; } = Array.Empty<CompilationDiagnostic>();

        private int ErrorsCount => this.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Error);

        private int WarningsCount => this.Diagnostics.Count(d => d.Severity == DiagnosticSeverity.Warning);

        private bool AreDiagnosticsShown { get; set; }

        private string LoaderText { get; set; }

        private bool Loading { get; set; }

        private bool ShowDiagnostics { get; set; }

        private void ToggleDiagnostics()
        {
            ShowDiagnostics = !ShowDiagnostics;
            AreDiagnosticsShown = ShowDiagnostics;
        }

        private string Version
        {
            get
            {
                var v = typeof(MudText).Assembly.GetName().Version;
                return $"v{v.Major}.{v.Minor}.{v.Build}";
            }
        }

        [JSInvokable]
        public async Task TriggerCompileAsync()
        {
            await this.CompileAsync();

            this.StateHasChanged();
        }

        public void Dispose()
        {
            this.dotNetInstance?.Dispose();
            this.JsRuntime.InvokeVoid(Try.Dispose);
        }

        protected override void OnAfterRender(bool firstRender)
        {
            if (firstRender)
            {
                this.dotNetInstance = DotNetObjectReference.Create(this);
                this.JsRuntime.InvokeVoid(Try.Initialize, this.dotNetInstance);
            }

            if (!string.IsNullOrWhiteSpace(this.errorMessage))
            {
                Snackbar.Add(this.errorMessage, Severity.Error);
                this.errorMessage = null;
            }

            base.OnAfterRender(firstRender);
        }

        protected override async Task OnInitializedAsync()
        {
            Snackbar.Clear();

            if (!string.IsNullOrWhiteSpace(this.SnippetId))
            {
                try
                {
                    this.CodeFiles = (await this.SnippetsService.GetSnippetContentAsync(this.SnippetId)).ToDictionary(f => f.Path, f => f);
                    if (!this.CodeFiles.Any())
                    {
                        this.errorMessage = "No files in snippet.";
                    }
                    else
                    {
                        this.activeCodeFile = this.CodeFiles.First().Value;
                    }
                }
                catch (ArgumentException)
                {
                    this.errorMessage = "Invalid Snippet ID.";
                }
                catch (Exception)
                {
                    this.errorMessage = "Unable to get snippet content. Please try again later.";
                }
            }

            if (!this.CodeFiles.Any())
            {
                this.activeCodeFile = new CodeFile
                {
                    Path = CoreConstants.MainComponentFilePath,
                    Content = CoreConstants.MainComponentDefaultFileContent,
                };
                this.CodeFiles.Add(CoreConstants.MainComponentFilePath, this.activeCodeFile);
            }

            this.CodeFileNames = this.CodeFiles.Keys.ToList();

            await base.OnInitializedAsync();
        }

        private async Task CompileAsync()
        {
            this.Loading = true;
            this.LoaderText = "Processing";

            await Task.Delay(10); // Ensure rendering has time to be called

            CompileToAssemblyResult compilationResult = null;
            CodeFile mainComponent = null;
            string originalMainComponentContent = null;
            try
            {
                this.UpdateActiveCodeFileContent();

                // Add the necessary main component code prefix and store the original content so we can revert right after compilation.
                if (this.CodeFiles.TryGetValue(CoreConstants.MainComponentFilePath, out mainComponent))
                {
                    originalMainComponentContent = mainComponent.Content;
                    mainComponent.Content = MainComponentCodePrefix + originalMainComponentContent.Replace(MainComponentCodePrefix, "");
                }

                compilationResult = await this.CompilationService.CompileToAssemblyAsync(
                    this.CodeFiles.Values,
                    this.UpdateLoaderTextAsync);

                this.Diagnostics = compilationResult.Diagnostics.OrderByDescending(x => x.Severity).ThenBy(x => x.Code).ToList();
                this.AreDiagnosticsShown = true;
            }
            catch (Exception e)
            {
                Console.WriteLine(e.Message);
                Snackbar.Add("Error while compiling the code.", Severity.Error);
            }
            finally
            {
                if (mainComponent != null)
                {
                    mainComponent.Content = originalMainComponentContent;
                }

                this.Loading = false;
            }

            if (compilationResult?.AssemblyBytes?.Length > 0)
            {
                // Make sure the DLL is updated before reloading the user page
                await this.JsRuntime.InvokeVoidAsync(Try.CodeExecution.UpdateUserComponentsDll, compilationResult.AssemblyBytes);

                // TODO: Add error page in iframe
                this.JsRuntime.InvokeVoid(Try.ReloadIframe, "user-page-window", MainUserPagePath);
            }
        }

        private void ShowSaveSnippetPopup() => this.SaveSnippetPopupVisible = true;

        private void HandleTabActivate(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            this.UpdateActiveCodeFileContent();

            if (this.CodeFiles.TryGetValue(name, out var codeFile))
            {
                this.activeCodeFile = codeFile;

                this.CodeEditorComponent.Focus();
            }
        }

        private void HandleTabClose(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            this.CodeFiles.Remove(name);
        }

        private void HandleTabCreate(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return;
            }

            var nameWithoutExtension = Path.GetFileNameWithoutExtension(name);

            var newCodeFile = new CodeFile { Path = name };

            newCodeFile.Content = newCodeFile.Type == CodeFileType.CSharp
                ? string.Format(CoreConstants.DefaultCSharpFileContentFormat, nameWithoutExtension)
                : string.Format(CoreConstants.DefaultRazorFileContentFormat, nameWithoutExtension);

            this.CodeFiles.TryAdd(name, newCodeFile);

            this.JsRuntime.InvokeVoid(Try.Editor.SetLangugage, newCodeFile.Type == CodeFileType.CSharp ? "csharp" : "razor");
        }

        private void UpdateActiveCodeFileContent()
        {
            if (this.activeCodeFile == null)
            {
                Snackbar.Add("No active file to update.", Severity.Error);
                return;
            }

            this.activeCodeFile.Content = this.CodeEditorComponent.GetCode();
        }

        private Task UpdateLoaderTextAsync(string loaderText)
        {
            this.LoaderText = loaderText;

            this.StateHasChanged();

            return Task.Delay(10); // Ensure rendering has time to be called
        }

        private async void UpdateTheme()
        {
            await LayoutService.ToggleDarkMode();
            string theme = LayoutService.IsDarkMode ? "vs-dark" : "default";
            this.JsRuntime.InvokeVoid(Try.Editor.SetTheme, theme);
            // LayoutService calls StateHasChanged, we need the updated <style> tags for updateIframeTheme to work
            await Task.Yield();
            await JsRuntime.InvokeVoidAsync("updateIframeTheme");
        }

        private async Task ClearCache()
        {
            await JsRuntime.InvokeVoidAsync("Try.clearCache");
            NavigationManager.NavigateTo(NavigationManager.BaseUri, true);
        }
    }
}
