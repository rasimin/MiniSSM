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

            // Register Monaco document formatting provider (Shift+Alt+F / Right-Click -> Format Document)
            monaco.languages.registerDocumentFormattingEditProvider('sql', {
                provideDocumentFormattingEdits: function(model) {
                    var text = model.getValue();
                    var formatted = formatSqlText(text);
                    return [{
                        range: model.getFullModelRange(),
                        text: formatted
                    }];
                }
            });

            var autoUppercaseKeywords = new Set([
                'select', 'from', 'where', 'group', 'order', 'having', 'union', 'all',
                'join', 'inner', 'left', 'right', 'full', 'outer', 'cross', 'apply', 'on',
                'insert', 'into', 'values', 'update', 'delete', 'set', 'truncate',
                'create', 'alter', 'drop', 'table', 'view', 'procedure', 'proc', 'function', 'index',
                'begin', 'end', 'try', 'catch', 'transaction', 'tran', 'commit', 'rollback',
                'case', 'when', 'then', 'else', 'and', 'or', 'not', 'null', 'is', 'in', 'exists',
                'distinct', 'top', 'with', 'as', 'over', 'partition', 'asc', 'desc', 'by',
                'exec', 'execute', 'declare', 'if', 'while', 'return', 'returns',
                'primary', 'key', 'foreign', 'references', 'identity', 'default', 'check', 'constraint',
                'clustered', 'nonclustered', 'nolock',
                'varchar', 'nvarchar', 'char', 'nchar', 'int', 'bigint', 'smallint', 'tinyint',
                'decimal', 'numeric', 'float', 'real', 'money', 'datetime', 'datetime2', 'date', 'time',
                'bit', 'uniqueidentifier', 'varbinary', 'binary', 'image', 'text', 'ntext', 'xml',
                'count', 'sum', 'avg', 'min', 'max', 'isnull', 'coalesce', 'getdate', 'sysdatetime',
                'cast', 'convert', 'row_number', 'rank', 'dense_rank', 'replace', 'trim', 'ltrim', 'rtrim',
                'dateadd', 'datediff', 'year', 'month', 'day', 'abs', 'round', 'floor', 'ceiling', 'iif'
            ]);

            var isAutoUppercasing = false;
            editor.onDidChangeModelContent(function(e) {
                if (!suppressChangeNotification) {
                    window.chrome.webview.postMessage({
                        action: 'contentChanged',
                        tabId: activeTabId
                    });
                }

                if (isAutoUppercasing || suppressChangeNotification) return;

                if (e.changes && e.changes.length > 0) {
                    var change = e.changes[0];
                    if (change.text === ' ' || change.text === '\n' || change.text === '\r\n' || change.text === '\t') {
                        var model = editor.getModel();
                        var pos = editor.getPosition();
                        if (!pos) return;

                        var lineText = model.getLineContent(pos.lineNumber);
                        var colBeforeSpace = change.text === ' ' ? pos.column - 1 : lineText.length + 1;
                        var textBefore = lineText.substring(0, colBeforeSpace - 1);

                        var match = textBefore.match(/([a-zA-Z_]+)$/);
                        if (match) {
                            var word = match[1];
                            var lowerWord = word.toLowerCase();

                            if (autoUppercaseKeywords.has(lowerWord) && word !== word.toUpperCase()) {
                                var wordStartCol = colBeforeSpace - word.length;
                                var range = new monaco.Range(pos.lineNumber, wordStartCol, pos.lineNumber, colBeforeSpace);
                                
                                isAutoUppercasing = true;
                                try {
                                    model.pushEditOperations([], [{
                                        range: range,
                                        text: word.toUpperCase()
                                    }], function() { return null; });
                                } finally {
                                    isAutoUppercasing = false;
                                }
                            }
                        }
                    }
                }
            });

            editor.onDidFocusEditorText(function() {
                window.chrome.webview.postMessage({ action: 'editorFocused', tabId: activeTabId });
            });

            // Send signal to C# on keydown inside Monaco for F5
            editor.addCommand(monaco.KeyCode.F5, function() {
                window.chrome.webview.postMessage({ action: 'execute', tabId: activeTabId });
            });

            editor.addCommand(monaco.KeyMod.CtrlCmd | monaco.KeyCode.KeyN, function() {
                window.chrome.webview.postMessage({ action: 'newQuery', tabId: activeTabId });
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


