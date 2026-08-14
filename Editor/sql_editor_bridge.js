        // C# callable functions for Multi-Model Monaco Editor:
        var suppressChangeNotification = false;

        function createTabModel(tabId, initialText) {
            if (!tabId) return;
            if (!tabModels.has(tabId)) {
                var uri = monaco.Uri.parse("inmemory://model/" + tabId + ".sql");
                var model = monaco.editor.getModel(uri) || monaco.editor.createModel(initialText || '', 'sql', uri);
                tabModels.set(tabId, model);

                model.onDidChangeContent(function() {
                    if (!suppressChangeNotification) {
                        window.chrome.webview.postMessage({
                            action: 'contentChanged',
                            tabId: tabId
                        });
                    }
                });
            }
        }

        function switchTabModel(tabId) {
            if (!editor || !tabId) return;

            if (activeTabId && activeTabId !== tabId && tabModels.has(activeTabId)) {
                var currentViewState = editor.saveViewState();
                if (currentViewState) {
                    tabViewStates.set(activeTabId, currentViewState);
                }
            }

            if (!tabModels.has(tabId)) {
                createTabModel(tabId, '');
            }

            activeTabId = tabId;
            var model = tabModels.get(tabId);
            suppressChangeNotification = true;
            try {
                editor.setModel(model);
                var viewState = tabViewStates.get(tabId);
                if (viewState) {
                    editor.restoreViewState(viewState);
                }
                editor.focus();
            } finally {
                suppressChangeNotification = false;
            }
        }

        function disposeTabModel(tabId) {
            if (!tabId) return;
            if (tabModels.has(tabId)) {
                var model = tabModels.get(tabId);
                model.dispose();
                tabModels.delete(tabId);
                tabViewStates.delete(tabId);
                if (activeTabId === tabId) {
                    activeTabId = null;
                }
            }
        }

        function getQueryText(tabId) {
            var targetModel = (tabId && tabModels.has(tabId)) ? tabModels.get(tabId) : (editor ? editor.getModel() : null);
            if (editor && (!tabId || tabId === activeTabId)) {
                var selection = editor.getSelection();
                if (selection) {
                    var selectedText = editor.getModel().getValueInRange(selection);
                    if (selectedText && selectedText.trim().length > 0) {
                        return selectedText;
                    }
                }
            }
            return targetModel ? targetModel.getValue() : '';
        }

        function getAllQueryText(tabId) {
            var targetModel = (tabId && tabModels.has(tabId)) ? tabModels.get(tabId) : (editor ? editor.getModel() : null);
            return targetModel ? targetModel.getValue() : '';
        }

        function setQueryText(text, tabId) {
            var targetModel = (tabId && tabModels.has(tabId)) ? tabModels.get(tabId) : (editor ? editor.getModel() : null);
            if (targetModel) {
                suppressChangeNotification = true;
                try {
                    targetModel.setValue(text || '');
                } finally {
                    suppressChangeNotification = false;
                }
            }
        }

        function focusEditor() {
            if (editor) {
                editor.focus();
            }
        }

        function insertTextAtCursor(text) {
            if (editor) {
                var selection = editor.getSelection();
                var range = new monaco.Range(
                    selection.startLineNumber,
                    selection.startColumn,
                    selection.endLineNumber,
                    selection.endColumn
                );
                var id = { major: 1, minor: 1 };
                var textEdit = { identifier: id, range: range, text: text, forceMoveMarkers: true };
                editor.executeEdits("my-source", [textEdit]);
                editor.focus();
            }
        }

        function addIdentityInsertWrapper(tableName) {
            if (!editor || !tableName) return;

            var model = editor.getModel();
            var selection = editor.getSelection();
            if (!model || !selection) return;

            var range = selection.isEmpty() ? model.getFullModelRange() : selection;
            var selectedText = model.getValueInRange(range);
            var wrappedText = 'SET IDENTITY_INSERT ' + tableName + ' ON;\n' +
                selectedText +
                (selectedText.endsWith('\n') ? '' : '\n') +
                'SET IDENTITY_INSERT ' + tableName + ' OFF;';

            editor.executeEdits('identity-insert-wrapper', [{
                range: range,
                text: wrappedText,
                forceMoveMarkers: true
            }]);
            editor.focus();
        }

        function getSelectedLineRange() {
            var selection = editor.getSelection();
            var startLine = selection.startLineNumber;
            var endLine = selection.endLineNumber;
            if (selection.endColumn === 1 && endLine > startLine) {
                endLine--;
            }
            return {
                selection: selection,
                startLine: startLine,
                endLine: endLine
            };
        }

        function updateSelectedLines(transformLine) {
            if (!editor) {
                return;
            }

            var model = editor.getModel();
            var rangeInfo = getSelectedLineRange();
            var edits = [];

            for (var lineNumber = rangeInfo.startLine; lineNumber <= rangeInfo.endLine; lineNumber++) {
                var oldText = model.getLineContent(lineNumber);
                var newText = transformLine(oldText);
                if (newText !== oldText) {
                    edits.push({
                        range: new monaco.Range(lineNumber, 1, lineNumber, oldText.length + 1),
                        text: newText
                    });
                }
            }

            if (edits.length > 0) {
                editor.executeEdits('toolbar-comment', edits);
            }

            editor.setSelection(rangeInfo.selection);
            editor.focus();
        }

        function commentSelection() {
            updateSelectedLines(function(line) {
                var indent = line.match(/^\s*/)[0];
                return indent + '-- ' + line.substring(indent.length);
            });
        }

        function uncommentSelection() {
            updateSelectedLines(function(line) {
                return line.replace(/^(\s*)--\s?/, '$1');
            });
        }

        var normalizedColumnCache = {};
        var normalizedColumnMapCache = {};

        function updateMetadata(meta) {
            meta = meta || {};
            tableColumns = meta.columns || {};
            objectTypes = meta.objectTypes || {};
            columnDetails = meta.columnDetails || {};
            storedProcedures = meta.storedProcedures || [];
            scalarFunctions = meta.scalarFunctions || [];
            tableFunctions = meta.tableFunctions || [];
            routineParameters = meta.routineParameters || {};
            foreignKeys = meta.foreignKeys || [];
            databases = meta.databases || [];
            activeDatabase = meta.activeDatabase || '';
            metadataLoaded = true;

            normalizedColumnCache = {};
            normalizedColumnMapCache = {};

            for (var k in tableColumns) {
                var cols = tableColumns[k];
                var clean = k.toLowerCase().replace(/[\[\]]/g, '');
                normalizedColumnCache[clean] = cols;
                if (clean.indexOf('.') > -1) {
                    var shortK = clean.split('.')[1];
                    if (!normalizedColumnCache[shortK]) {
                        normalizedColumnCache[shortK] = cols;
                    }
                }

                var colMap = new Map();
                if (cols && cols.length > 0) {
                    cols.forEach(c => colMap.set(c.toLowerCase(), c));
                }
                normalizedColumnMapCache[clean] = colMap;
                if (clean.indexOf('.') > -1) {
                    var shortK = clean.split('.')[1];
                    if (!normalizedColumnMapCache[shortK]) {
                        normalizedColumnMapCache[shortK] = colMap;
                    }
                }
            }

            var uniqueTables = new Set();
            for (var tbl in tableColumns) {
                if (tbl.indexOf('.') > -1) {
                    uniqueTables.add(tbl);
                } else {
                    var hasSchemaVersion = Object.keys(tableColumns).some(other => other.indexOf('.') > -1 && other.endsWith('.' + tbl));
                    if (!hasSchemaVersion) {
                        uniqueTables.add(tbl);
                    }
                }
            }
            tables = Array.from(uniqueTables);

            var schemaSet = new Set();
            tables.forEach(t => {
                if (t.indexOf('.') > -1) {
                    schemaSet.add(t.split('.')[0]);
                }
            });
            scalarFunctions.concat(tableFunctions).forEach(functionName => {
                if (functionName.indexOf('.') > -1) {
                    schemaSet.add(functionName.split('.')[0]);
                }
            });
            schemas = Array.from(schemaSet);
        }

        function gotoLine(lineNumber) {
            if (!editor) return;
            var lineNum = parseInt(lineNumber, 10);
            if (isNaN(lineNum) || lineNum < 1) return;
            editor.setPosition({ lineNumber: lineNum, column: 1 });
            editor.revealLineInCenter(lineNum);
            editor.focus();
        }

        window.chrome.webview.addEventListener('message', function (e) {
            if (!e.data) return;
            if (e.data.action === 'updateMetadata') {
                updateMetadata(e.data.payload);
            } else if (e.data.action === 'createTabModel') {
                createTabModel(e.data.tabId, e.data.initialText);
            } else if (e.data.action === 'switchTabModel') {
                switchTabModel(e.data.tabId);
            } else if (e.data.action === 'disposeTabModel') {
                disposeTabModel(e.data.tabId);
            } else if (e.data.action === 'gotoLine') {
                gotoLine(e.data.lineNumber);
            } else if (e.data.action === 'replaceCurrentLineWithScript') {
                if (!editor) return;
                var script = e.data.script;
                if (!script) return;

                var position = editor.getPosition();
                var model = editor.getModel();
                if (!position || !model) return;

                var lineNumber = position.lineNumber;
                var lineRange = new monaco.Range(lineNumber, 1, lineNumber, model.getLineMaxColumn(lineNumber));

                editor.executeEdits('fetchObjectScript', [{
                    range: lineRange,
                    text: script,
                    forceMoveMarkers: true
                }]);

                editor.setPosition({ lineNumber: lineNumber, column: 1 });
                editor.revealLineInCenter(lineNumber);
                editor.focus();
            }
        });

