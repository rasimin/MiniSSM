        require(['vs/editor/editor.main'], function () {
            registerSqlCompletionProvider();
            registerSqlHoverProvider();

            // Initialize Monaco Editor
            editor = monaco.editor.create(document.getElementById('container'), {
                value: '',
                language: 'sql',
                theme: 'vs-dark',
                automaticLayout: true,
                fontSize: 14,
                fontFamily: 'Consolas, Courier New, monospace',
                acceptSuggestionOnEnter: 'smart',
                wordBasedSuggestions: false,
                minimap: { enabled: false },
                scrollbar: {
                    verticalScrollbarSize: 6,
                    horizontalScrollbarSize: 6,
                    useShadows: false
                }
            });

            editor.onDidChangeModelContent(function() {
                if (!suppressChangeNotification) {
                    window.chrome.webview.postMessage({
                        action: 'contentChanged'
                    });
                }
            });

            editor.onDidFocusEditorText(function() {
                window.chrome.webview.postMessage({ action: 'editorFocused' });
            });

            // Send signal to C# on keydown inside Monaco for F5
            editor.addCommand(monaco.KeyCode.F5, function() {
                window.chrome.webview.postMessage({ action: 'execute' });
            });

            editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyN, function() {
                window.chrome.webview.postMessage({ action: 'newQuery' });
            });

            // WebView2 can consume Ctrl+Space before Monaco's default keybinding runs.
            // Register it explicitly so manual autocomplete always opens.
            editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.Space, function() {
                editor.trigger('keyboard', 'editor.action.triggerSuggest', {});
            });

            editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyMod.Shift | monaco.KeyCode.KeyF, function() {
                formatSql();
            });

            window.chrome.webview.postMessage({ action: 'editorReady' });
        });

