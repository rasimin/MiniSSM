        // C# callable functions:
        var suppressChangeNotification = false;

        function getQueryText() {
            if (editor) {
                var selection = editor.getSelection();
                var selectedText = editor.getModel().getValueInRange(selection);
                if (selectedText && selectedText.trim().length > 0) {
                    return selectedText;
                }
                return editor.getValue();
            }
            return '';
        }

        function getAllQueryText() {
            return editor ? editor.getValue() : '';
        }

        function setQueryText(text) {
            if (editor) {
                suppressChangeNotification = true;
                try {
                    editor.setValue(text);
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

        function updateMetadata(meta) {
            meta = meta || {};
            tableColumns = meta.columns || {};
            objectTypes = meta.objectTypes || {};
            columnDetails = meta.columnDetails || {};
            storedProcedures = meta.storedProcedures || [];
            scalarFunctions = meta.scalarFunctions || [];
            tableFunctions = meta.tableFunctions || [];
            routineParameters = meta.routineParameters || {};
            databases = meta.databases || [];
            activeDatabase = meta.activeDatabase || '';
            metadataLoaded = true;
            tables = Object.keys(tableColumns);
            
            // Extract unique schemas from keys (e.g. "dbo" from "dbo.customers")
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
    

