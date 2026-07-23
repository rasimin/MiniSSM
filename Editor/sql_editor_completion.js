        function registerSqlCompletionProvider() {
            // Register custom completion item provider for sql
            monaco.languages.registerCompletionItemProvider('sql', {
                triggerCharacters: ['.'],
                provideCompletionItems: function (model, position) {
                    if (!metadataLoaded) {
                        window.chrome.webview.postMessage({ action: 'requestMetadata' });
                    }
                    var word = model.getWordUntilPosition(position);
                    var range = {
                        startLineNumber: position.lineNumber,
                        endLineNumber: position.lineNumber,
                        startColumn: word.startColumn,
                        endColumn: word.endColumn
                    };
                    
                    var lineText = model.getLineContent(position.lineNumber);
                    var textBeforeCursor = lineText.substring(0, position.column - 1);
                    var isAfterDot = textBeforeCursor.endsWith('.');
                    
                    var suggestions = [];
                    var fullSql = model.getValue();
                    var sourceContext = /\b(?:FROM|JOIN|APPLY)\s+[\[\]a-zA-Z0-9_.#]*$/i.test(textBeforeCursor);
                    var cursorOffset = model.getOffsetAt(position);
                    var activeSqlInfo = getActiveSqlText(model, position);
                    var activeSql = activeSqlInfo.text;
                    var activeCursorOffset = activeSqlInfo.cursorOffset;
                    var includeFollowingSources = isSelectListContext(activeSql, activeCursorOffset);
                    var sourceLookupOffset = includeFollowingSources ? undefined : activeCursorOffset;
                    getLocalObjects(fullSql);
                    var querySources = getQuerySources(activeSql, sourceLookupOffset);

                    // Suggest routine parameters after EXEC proc or inside a function call.
                    var routineContext = textBeforeCursor.match(/\bEXEC(?:UTE)?\s+([\[\]a-zA-Z0-9_.]+)\s+(?:[^;]*)$/i) ||
                        textBeforeCursor.match(/([\[\]a-zA-Z0-9_.]+)\s*\([^)]*$/i);
                    if (routineContext) {
                        var routineName = routineContext[1].replace(/[\[\]]/g, '');
                        var parameters = routineParameters[routineName];
                        if (!parameters) {
                            Object.keys(routineParameters).some(key => {
                                if (key.toLowerCase().endsWith('.' + routineName.toLowerCase())) {
                                    parameters = routineParameters[key];
                                    return true;
                                }
                                return false;
                            });
                        }
                        if (parameters) {
                            parameters.forEach(parameterName => {
                                suggestions.push({
                                    label: parameterName,
                                    kind: monaco.languages.CompletionItemKind.Variable,
                                    insertText: parameterName,
                                    detail: "Routine Parameter (" + routineName + ")",
                                    sortText: "0_" + parameterName,
                                    range: range
                                });
                            });
                            return { suggestions: suggestions };
                        }
                    }

                    // After EXEC/EXECUTE, suggest stored procedures instead of tables/columns.
                    var execMatch = textBeforeCursor.match(/\bEXEC(?:UTE)?\s+([a-zA-Z0-9_.]*)$/i);
                    if (execMatch) {
                        var procedureToken = execMatch[1];
                        var schemaPrefix = procedureToken.indexOf('.') > -1
                            ? procedureToken.substring(0, procedureToken.lastIndexOf('.') + 1)
                            : '';

                        storedProcedures.forEach(procedureName => {
                            if (schemaPrefix && !procedureName.toLowerCase().startsWith(schemaPrefix.toLowerCase())) {
                                return;
                            }

                            var insertName = schemaPrefix
                                ? procedureName.substring(procedureName.lastIndexOf('.') + 1)
                                : procedureName;

                            suggestions.push({
                                label: procedureName,
                                kind: monaco.languages.CompletionItemKind.Function,
                                insertText: insertName,
                                detail: "Stored Procedure",
                                sortText: "0_" + procedureName,
                                range: range
                            });
                        });

                        return { suggestions: suggestions };
                    }

                    if (isAfterDot) {
                        var crossDatabaseMatch = textBeforeCursor.match(/(?:\[([^\]]+)\]|([a-zA-Z0-9_]+))\.(?:\[([^\]]+)\]|([a-zA-Z0-9_]+))\.$/);
                        if (crossDatabaseMatch) {
                            var crossDatabase = crossDatabaseMatch[1] || crossDatabaseMatch[2];
                            var crossSchema = crossDatabaseMatch[3] || crossDatabaseMatch[4];
                            var crossMeta = databaseMetadata[crossDatabase.toLowerCase()];
                            if (!crossMeta) {
                                requestDatabaseMetadata(crossDatabase);
                                return { suggestions: [] };
                            }
                            Object.keys(crossMeta.columns || {}).forEach(objectName => {
                                if (sourceContext && objectName.toLowerCase().startsWith(crossSchema.toLowerCase() + '.')) {
                                    var shortName = objectName.substring(crossSchema.length + 1);
                                    suggestions.push({
                                        label: shortName,
                                        kind: monaco.languages.CompletionItemKind.Class,
                                        insertText: shortName,
                                        detail: (crossMeta.objectTypes[objectName] || "Table") + " (" + crossDatabase + "." + crossSchema + ")",
                                        range: range
                                    });
                                }
                            });
                            var crossFunctions = sourceContext
                                ? (crossMeta.tableFunctions || [])
                                : (crossMeta.scalarFunctions || []);
                            crossFunctions.forEach(functionName => {
                                if (functionName.toLowerCase().startsWith(crossSchema.toLowerCase() + '.')) {
                                    suggestions.push({
                                        label: functionName.substring(crossSchema.length + 1),
                                        kind: monaco.languages.CompletionItemKind.Function,
                                        insertText: functionName.substring(crossSchema.length + 1),
                                        detail: (sourceContext ? "Table-valued" : "Scalar") + " Function (" + crossDatabase + "." + crossSchema + ")",
                                        range: range
                                    });
                                }
                            });
                            return { suggestions: suggestions };
                        }

                        // Find the word preceding the dot
                        var match = textBeforeCursor.match(/(\[?[a-zA-Z0-9_\u00c0-\u00ff]+\]?)\.$/);
                        if (match) {
                            var identifier = match[1].replace(/[\[\]]/g, '');

                            var databaseName = databases.find(name => name.toLowerCase() === identifier.toLowerCase());
                            if (databaseName && databaseName.toLowerCase() !== activeDatabase.toLowerCase()) {
                                var databaseMeta = databaseMetadata[databaseName.toLowerCase()];
                                if (!databaseMeta) {
                                    requestDatabaseMetadata(databaseName);
                                    return { suggestions: [] };
                                }
                                var databaseSchemas = new Set();
                                Object.keys(databaseMeta.columns || {}).forEach(name => databaseSchemas.add(name.split('.')[0]));
                                databaseSchemas.forEach(schemaName => {
                                    suggestions.push({
                                        label: schemaName,
                                        kind: monaco.languages.CompletionItemKind.Module,
                                        insertText: schemaName,
                                        detail: "Schema (" + databaseName + ")",
                                        range: range
                                    });
                                });
                                return { suggestions: suggestions };
                            }

                            // 1. Check if the identifier is a Schema
                            if (schemas.indexOf(identifier) > -1) {
                                // List tables under this schema
                                tables.forEach(t => {
                                    if (sourceContext && t.toLowerCase().startsWith((identifier + '.').toLowerCase())) {
                                        var tableNameOnly = t.substring(identifier.length + 1);
                                        suggestions.push({
                                            label: tableNameOnly,
                                            kind: monaco.languages.CompletionItemKind.Class,
                                            insertText: tableNameOnly,
                                            detail: (objectTypes[t] || "Table") + " (" + identifier + ")",
                                            range: range
                                        });
                                    }
                                });

                                scalarFunctions.forEach(functionName => {
                                    if (!sourceContext && functionName.toLowerCase().startsWith((identifier + '.').toLowerCase())) {
                                        var shortName = functionName.substring(identifier.length + 1);
                                        suggestions.push({
                                            label: shortName,
                                            kind: monaco.languages.CompletionItemKind.Function,
                                            insertText: shortName,
                                            detail: "Scalar Function (" + identifier + ")",
                                            range: range
                                        });
                                    }
                                });

                                tableFunctions.forEach(functionName => {
                                    if (sourceContext && functionName.toLowerCase().startsWith((identifier + '.').toLowerCase())) {
                                        var shortName = functionName.substring(identifier.length + 1);
                                        suggestions.push({
                                            label: shortName,
                                            kind: monaco.languages.CompletionItemKind.Function,
                                            insertText: shortName,
                                            detail: "Table-valued Function (" + identifier + ")",
                                            range: range
                                        });
                                    }
                                });
                                return { suggestions: suggestions };
                            }

                            // 2. Check if the identifier is a Table directly
                            var columns = tableColumns[identifier];
                            var matchedSource = querySources.find(source =>
                                source.qualifier.toLowerCase() === identifier.toLowerCase() ||
                                source.objectName.toLowerCase() === identifier.toLowerCase());
                            var columnSource = {
                                qualifier: identifier,
                                objectName: identifier,
                                sourceLabel: identifier,
                                displayName: identifier,
                                isAlias: false
                            };
                            if (matchedSource) {
                                columnSource = matchedSource;
                            }

                            // 3. Check if the identifier is a Table Alias
                            if (!columns) {
                                var realTableName = getTableForAlias(identifier, activeSql, sourceLookupOffset);
                                if (realTableName) {
                                    columns = tableColumns[realTableName];
                                    columnSource = {
                                        qualifier: identifier,
                                        objectName: realTableName,
                                        sourceLabel: realTableName,
                                        displayName: identifier + " (" + realTableName + ")",
                                        isAlias: true
                                    };
                                }
                            }

                            // If columns found, show column suggestions
                            if (columns) {
                                columns.forEach(col => {
                                    suggestions.push(createColumnSuggestion(col, columnSource, range, true));
                                });
                                return { suggestions: suggestions };
                            }
                        }
                    }

                    // Check if cursor is right after an asterisk "*" (for wildcard column list expansion)
                    var textTrimmed = textBeforeCursor.trim();
                    var endsWithAsterisk = textTrimmed.endsWith('*') && !textTrimmed.endsWith('.*');

                    if (endsWithAsterisk) {
                        var queryTables = getTablesInQuery(model.getValue());
                        queryTables.forEach(qt => {
                            var cols = findColumns(qt);
                            if (cols) {
                                var columnList = cols.join(', ');
                                suggestions.push({
                                    label: "* (Expand Columns - " + qt + ")",
                                    kind: monaco.languages.CompletionItemKind.Snippet,
                                    insertText: columnList,
                                    detail: "Expand wildcard * to column list of " + qt,
                                    range: {
                                        startLineNumber: position.lineNumber,
                                        endLineNumber: position.lineNumber,
                                        startColumn: textBeforeCursor.lastIndexOf('*') + 1, // Overwrite the asterisk character
                                        endColumn: position.column
                                    }
                                });
                            }
                        });
                        if (suggestions.length > 0) {
                            return { suggestions: suggestions };
                        }
                    }

                    // Otherwise, provide general suggestions (Keywords, Snippets, Schemas, and Tables)

                    var localObjects = getLocalObjects(fullSql);
                    Object.keys(localObjects).forEach(objectName => {
                        suggestions.push({
                            label: objectName,
                            kind: monaco.languages.CompletionItemKind.Struct,
                            insertText: objectName,
                            detail: localObjects[objectName].type,
                            sortText: (sourceContext ? "0_" : "1_") + objectName,
                            range: range
                        });
                    });
                    
                    // 1. Snippet ssf: SELECT TOP 50 * FROM
                    suggestions.push({
                        label: "ssf",
                        kind: monaco.languages.CompletionItemKind.Snippet,
                        insertText: "SELECT TOP 50 * FROM $0",
                        insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
                        detail: "Snippet: SELECT TOP 50 * FROM",
                        sortText: "8_ssf",
                        range: range
                    });

                    // 2. Columns of tables present in the current query (Smart Autocomplete like Redgate)
                    querySources.forEach(source => {
                        var cols = findColumns(source.objectName);
                        if (cols) {
                            cols.forEach(col => {
                                suggestions.push(createColumnSuggestion(col, source, range, false));
                            });
                        }
                    });

                    // 3. Standard T-SQL keywords, data types, and built-in functions.
                    tsqlKeywords.forEach(kw => {
                        suggestions.push({
                            label: kw,
                            kind: monaco.languages.CompletionItemKind.Keyword,
                            insertText: kw,
                            detail: "T-SQL Keyword",
                            sortText: "3_" + kw,
                            range: range
                        });
                    });

                    tsqlDataTypes.forEach(dataType => {
                        suggestions.push({
                            label: dataType,
                            kind: monaco.languages.CompletionItemKind.TypeParameter,
                            insertText: dataType,
                            detail: "T-SQL Data Type",
                            sortText: "4_" + dataType,
                            range: range
                        });
                    });

                    if (!sourceContext) {
                        tsqlBuiltInFunctions.forEach(functionInfo => {
                            suggestions.push({
                                label: functionInfo.name + "()",
                                filterText: functionInfo.name,
                                kind: monaco.languages.CompletionItemKind.Function,
                                insertText: functionInfo.name + "(" + functionInfo.args + ")",
                                insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
                                detail: "Built-in T-SQL Function",
                                sortText: "2_" + functionInfo.name,
                                range: range
                            });
                        });
                    }

                    // 4. Schemas
                    schemas.forEach(s => {
                        suggestions.push({
                            label: s,
                            kind: monaco.languages.CompletionItemKind.Module,
                            insertText: s,
                            detail: "Schema",
                            sortText: (sourceContext ? "1_" : "5_") + s,
                            range: range
                        });
                    });

                    // 5. Tables
                    if (sourceContext || textBeforeCursor.trim().length === 0) {
                        tables.forEach(t => {
                            suggestions.push({
                                label: t,
                                kind: monaco.languages.CompletionItemKind.Class,
                                insertText: t,
                                detail: objectTypes[t] || "Table",
                                sortText: "0_" + t,
                                range: range
                            });
                        });
                    }

                    // 6. Scalar and table-valued functions
                    if (!sourceContext) {
                        scalarFunctions.forEach(functionName => {
                            suggestions.push({
                                label: functionName,
                                kind: monaco.languages.CompletionItemKind.Function,
                                insertText: functionName,
                                detail: "Scalar Function",
                                sortText: "2_" + functionName,
                                range: range
                            });
                        });
                    }

                    if (sourceContext) {
                        tableFunctions.forEach(functionName => {
                            suggestions.push({
                                label: functionName,
                                kind: monaco.languages.CompletionItemKind.Function,
                                insertText: functionName,
                                detail: "Table-valued Function",
                                sortText: "1_" + functionName,
                                range: range
                            });
                        });
                    }

                    databases.forEach(databaseName => {
                        suggestions.push({
                            label: databaseName,
                            kind: monaco.languages.CompletionItemKind.Module,
                            insertText: '[' + databaseName + ']',
                            detail: databaseName === activeDatabase ? "Active Database" : "Database",
                            sortText: (sourceContext ? "2_" : "6_") + databaseName,
                            range: range
                        });
                    });

                    return { suggestions: suggestions };
                }
            });
        }

