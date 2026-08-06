        function registerSqlCompletionProvider() {
            if (monaco.editor.registerCommand) {
                try {
                    monaco.editor.registerCommand("fetchObjectScriptCommand", function (accessor, objectName, statementType) {
                        window.chrome.webview.postMessage({
                            action: 'fetchObjectScript',
                            tabId: activeTabId,
                            objectName: objectName,
                            statementType: statementType || 'ALTER'
                        });
                    });
                } catch (e) { }
            }

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
                    var sourceContext = /\b(?:FROM|JOIN|APPLY|INSERT(?:\s+INTO)?|INTO|UPDATE|DELETE(?:\s+FROM)?|TRUNCATE(?:\s+TABLE)?|ALTER\s+TABLE|DROP\s+TABLE|CREATE\s+TABLE|MERGE(?:\s+INTO)?|TABLE)\s+[\[\]a-zA-Z0-9_.#]*$/i.test(textBeforeCursor);
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

                    // 0. Check if user is typing after ON (e.g. "FROM TTransaction a INNER JOIN TCustomer b ON ")
                    var onMatch = textBeforeCursor.match(/\b(?:JOIN|APPLY)\s+([#a-zA-Z0-9_\.\[\]]+)(?:\s+(?:AS\s+)?([a-zA-Z0-9_]+))?\s+ON\s*([a-zA-Z0-9_\.]*)$/i);
                    if (onMatch) {
                        var joinedObj = onMatch[1].replace(/[\[\]]/g, '');
                        var joinedAlias = onMatch[2] ? onMatch[2].replace(/[\[\]]/g, '') : generateTableAlias(joinedObj);

                        var onSuggestions = getOnConditionSuggestions(joinedObj, joinedAlias, querySources, range);
                        if (onSuggestions && onSuggestions.length > 0) {
                            onSuggestions.forEach(s => suggestions.push(s));
                        }
                        return { suggestions: suggestions };
                    }

                    var isAlterProc = /\bALTER\s+PROCEDURE\s+[a-zA-Z0-9_.]*$/i.test(textBeforeCursor);
                    var isAlterView = /\bALTER\s+VIEW\s+[a-zA-Z0-9_.]*$/i.test(textBeforeCursor);
                    var isAlterFunc = /\bALTER\s+FUNCTION\s+[a-zA-Z0-9_.]*$/i.test(textBeforeCursor);
                    var isAlterTable = /\bALTER\s+TABLE\s+[a-zA-Z0-9_.]*$/i.test(textBeforeCursor);

                    // 1. After EXEC/EXECUTE or ALTER/CREATE/DROP PROCEDURE, suggest stored procedures
                    var procMatch = textBeforeCursor.match(/\b(?:EXEC|EXECUTE|ALTER\s+PROCEDURE|CREATE\s+PROCEDURE|DROP\s+PROCEDURE)\s+([a-zA-Z0-9_.]*)$/i);
                    if (procMatch) {
                        var procedureToken = procMatch[1];
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

                            var item = {
                                label: procedureName,
                                kind: monaco.languages.CompletionItemKind.Function,
                                insertText: insertName,
                                detail: "Stored Procedure",
                                sortText: "0_" + procedureName,
                                range: range
                            };
                            if (isAlterProc) {
                                item.command = {
                                    id: "fetchObjectScriptCommand",
                                    title: "Fetch Script",
                                    arguments: [procedureName, "ALTER"]
                                };
                            }
                            suggestions.push(item);
                        });

                        return { suggestions: suggestions };
                    }

                    // 2. After ALTER VIEW / CREATE VIEW / DROP VIEW
                    var viewMatch = textBeforeCursor.match(/\b(?:ALTER\s+VIEW|CREATE\s+VIEW|DROP\s+VIEW)\s+([a-zA-Z0-9_.]*)$/i);
                    if (viewMatch) {
                        var viewToken = viewMatch[1];
                        var schemaPrefix = viewToken.indexOf('.') > -1
                            ? viewToken.substring(0, viewToken.lastIndexOf('.') + 1)
                            : '';

                        tables.forEach(t => {
                            if ((objectTypes[t] || '').toLowerCase().includes('view')) {
                                if (schemaPrefix && !t.toLowerCase().startsWith(schemaPrefix.toLowerCase())) return;
                                var insertName = schemaPrefix ? t.substring(t.lastIndexOf('.') + 1) : t;
                                var item = {
                                    label: t,
                                    kind: monaco.languages.CompletionItemKind.Interface,
                                    insertText: insertName,
                                    detail: "View",
                                    sortText: "0_" + t,
                                    range: range
                                };
                                if (isAlterView) {
                                    item.command = {
                                        id: "fetchObjectScriptCommand",
                                        title: "Fetch Script",
                                        arguments: [t, "ALTER"]
                                    };
                                }
                                suggestions.push(item);
                            }
                        });

                        return { suggestions: suggestions };
                    }

                    // 3. After ALTER FUNCTION / CREATE FUNCTION / DROP FUNCTION
                    var funcMatch = textBeforeCursor.match(/\b(?:ALTER\s+FUNCTION|CREATE\s+FUNCTION|DROP\s+FUNCTION)\s+([a-zA-Z0-9_.]*)$/i);
                    if (funcMatch) {
                        var funcToken = funcMatch[1];
                        var schemaPrefix = funcToken.indexOf('.') > -1
                            ? funcToken.substring(0, funcToken.lastIndexOf('.') + 1)
                            : '';

                        var allFuncs = scalarFunctions.concat(tableFunctions);
                        allFuncs.forEach(functionName => {
                            if (schemaPrefix && !functionName.toLowerCase().startsWith(schemaPrefix.toLowerCase())) return;
                            var insertName = schemaPrefix ? functionName.substring(functionName.lastIndexOf('.') + 1) : functionName;
                            var item = {
                                label: functionName,
                                kind: monaco.languages.CompletionItemKind.Function,
                                insertText: insertName,
                                detail: "Function",
                                sortText: "0_" + functionName,
                                range: range
                            };
                            if (isAlterFunc) {
                                item.command = {
                                    id: "fetchObjectScriptCommand",
                                    title: "Fetch Script",
                                    arguments: [functionName, "ALTER"]
                                };
                            }
                            suggestions.push(item);
                        });

                        return { suggestions: suggestions };
                    }

                    // 4. After ALTER TABLE / DROP TABLE / TRUNCATE TABLE
                    var tableMatch = textBeforeCursor.match(/\b(?:ALTER\s+TABLE|DROP\s+TABLE|TRUNCATE\s+TABLE)\s+([a-zA-Z0-9_.]*)$/i);
                    if (tableMatch) {
                        var tableToken = tableMatch[1];
                        var schemaPrefix = tableToken.indexOf('.') > -1
                            ? tableToken.substring(0, tableToken.lastIndexOf('.') + 1)
                            : '';

                        tables.forEach(t => {
                            if (schemaPrefix && !t.toLowerCase().startsWith(schemaPrefix.toLowerCase())) return;
                            var insertName = schemaPrefix ? t.substring(t.lastIndexOf('.') + 1) : t;
                            var item = {
                                label: t,
                                kind: monaco.languages.CompletionItemKind.Class,
                                insertText: insertName,
                                detail: objectTypes[t] || "Table",
                                sortText: "0_" + t,
                                range: range
                            };
                            if (isAlterTable) {
                                item.command = {
                                    id: "fetchObjectScriptCommand",
                                    title: "Fetch Script",
                                    arguments: [t, "ALTER"]
                                };
                            }
                            suggestions.push(item);
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
                                range: range
                            });
                        });
                    
                    // Check if a full table name + space has been typed after INSERT INTO or UPDATE
                    var lineUntilCursor = lineText.substring(0, position.column - 1);
                    var insertSpaceMatch = lineUntilCursor.match(/\bINSERT(?:\s+INTO)?\s+([#a-zA-Z0-9_\.\[\]]+)\s+$/i);
                    if (insertSpaceMatch) {
                        var typedTable = insertSpaceMatch[1].replace(/[\[\]]/g, '');
                        var matchedTable = tables.find(t => t.toLowerCase() === typedTable.toLowerCase() || t.toLowerCase().endsWith('.' + typedTable.toLowerCase()));
                        var targetTable = matchedTable || typedTable;
                        var snippet = generateInsertSnippet(targetTable);
                        if (snippet) {
                            var matchStartCol = lineUntilCursor.lastIndexOf(insertSpaceMatch[0]) + 1;
                            suggestions.push({
                                label: "Snippet: INSERT into " + targetTable + " (All columns)",
                                kind: monaco.languages.CompletionItemKind.Snippet,
                                insertText: snippet,
                                insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
                                detail: "Generate INSERT script template with field values and type remarks for " + targetTable,
                                sortText: "0_0_insert_" + targetTable,
                                range: {
                                    startLineNumber: position.lineNumber,
                                    endLineNumber: position.lineNumber,
                                    startColumn: matchStartCol,
                                    endColumn: position.column
                                }
                            });
                        }
                    }

                    var updateSpaceMatch = lineUntilCursor.match(/\bUPDATE\s+([#a-zA-Z0-9_\.\[\]]+)(?:\s+SET)?\s*$/i);
                    if (updateSpaceMatch && !insertSpaceMatch && /\bUPDATE\s+[#a-zA-Z0-9_\.\[\]]+\s+/i.test(lineUntilCursor)) {
                        var typedUpdateTable = updateSpaceMatch[1].replace(/[\[\]]/g, '');
                        var matchedUpdateTable = tables.find(t => t.toLowerCase() === typedUpdateTable.toLowerCase() || t.toLowerCase().endsWith('.' + typedUpdateTable.toLowerCase()));
                        var targetUpdateTable = matchedUpdateTable || typedUpdateTable;
                        var updateSnippet = generateUpdateSnippet(targetUpdateTable);
                        if (updateSnippet) {
                            var updateStartCol = lineUntilCursor.lastIndexOf(updateSpaceMatch[0]) + 1;
                            suggestions.push({
                                label: "Snippet: UPDATE " + targetUpdateTable + " SET (All columns)",
                                kind: monaco.languages.CompletionItemKind.Snippet,
                                insertText: updateSnippet,
                                insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
                                detail: "Generate UPDATE script template with PK & type remarks for " + targetUpdateTable,
                                sortText: "0_0_update_" + targetUpdateTable,
                                range: {
                                    startLineNumber: position.lineNumber,
                                    endLineNumber: position.lineNumber,
                                    startColumn: updateStartCol,
                                    endColumn: position.column
                                }
                            });
                        }
                    }
                    
                    // Redgate SQL Prompt Snippets
                    var redgateSnippets = [
                        // ALTER Snippets (insert statement and trigger object autocomplete)
                        { label: "ap", insertText: "ALTER PROCEDURE ", detail: "Redgate Snippet: ALTER PROCEDURE (Suggest Stored Procedures)", command: { id: "editor.action.triggerSuggest", title: "Trigger Suggest" } },
                        { label: "av", insertText: "ALTER VIEW ", detail: "Redgate Snippet: ALTER VIEW (Suggest Views)", command: { id: "editor.action.triggerSuggest", title: "Trigger Suggest" } },
                        { label: "af", insertText: "ALTER FUNCTION ", detail: "Redgate Snippet: ALTER FUNCTION (Suggest Functions)", command: { id: "editor.action.triggerSuggest", title: "Trigger Suggest" } },
                        { label: "at", insertText: "ALTER TABLE ", detail: "Redgate Snippet: ALTER TABLE (Suggest Tables)", command: { id: "editor.action.triggerSuggest", title: "Trigger Suggest" } },

                        // DROP Snippets (insert statement and trigger object autocomplete)
                        { label: "dp", insertText: "DROP PROCEDURE ", detail: "Redgate Snippet: DROP PROCEDURE (Suggest Stored Procedures)", command: { id: "editor.action.triggerSuggest", title: "Trigger Suggest" } },
                        { label: "dv", insertText: "DROP VIEW ", detail: "Redgate Snippet: DROP VIEW (Suggest Views)", command: { id: "editor.action.triggerSuggest", title: "Trigger Suggest" } },
                        { label: "dfn", insertText: "DROP FUNCTION ", detail: "Redgate Snippet: DROP FUNCTION (Suggest Functions)", command: { id: "editor.action.triggerSuggest", title: "Trigger Suggest" } },
                        { label: "dt", insertText: "DROP TABLE ", detail: "Redgate Snippet: DROP TABLE (Suggest Tables)", command: { id: "editor.action.triggerSuggest", title: "Trigger Suggest" } },

                        // CREATE Snippets
                        { label: "cp", insertText: "CREATE PROCEDURE ${1:dbo.sp_name}\nAS\nBEGIN\n\tSET NOCOUNT ON;\n\t$0\nEND\nGO", detail: "Redgate Snippet: CREATE PROCEDURE" },
                        { label: "ct", insertText: "CREATE TABLE ${1:dbo.TableName} (\n\t${2:ID} INT IDENTITY(1,1) NOT NULL PRIMARY KEY,\n\t${3:ColumnName} VARCHAR(50) NULL\n);$0", detail: "Redgate Snippet: CREATE TABLE" },
                        { label: "cv", insertText: "CREATE VIEW ${1:dbo.ViewName}\nAS\nSELECT ${2:*}\nFROM ${3:dbo.TableName};$0", detail: "Redgate Snippet: CREATE VIEW" },
                        { label: "cf", insertText: "CREATE FUNCTION ${1:dbo.FunctionName} (\n\t@${2:Param1} INT\n)\nRETURNS ${3:INT}\nAS\nBEGIN\n\tRETURN ${4:0};\nEND\nGO", detail: "Redgate Snippet: CREATE FUNCTION" },

                        // DML Snippets
                        { label: "ssf", insertText: "SELECT TOP 50 * FROM $0", detail: "Redgate Snippet: SELECT TOP 50 * FROM" },
                        { label: "sf", insertText: "SELECT * FROM $0", detail: "Redgate Snippet: SELECT * FROM" },
                        { label: "se", insertText: "SELECT $0", detail: "Redgate Snippet: SELECT" },
                        { label: "ii", insertText: "INSERT INTO $0", detail: "Redgate Snippet: INSERT INTO" },
                        { label: "ud", insertText: "UPDATE ${1:TableName} SET ${2:ColumnName} = ${3:Value} WHERE ${4:Condition};$0", detail: "Redgate Snippet: UPDATE" },
                        { label: "df", insertText: "DELETE FROM ${1:TableName} WHERE ${2:Condition};$0", detail: "Redgate Snippet: DELETE FROM" },

                        // Join Snippets
                        { label: "ij", insertText: "INNER JOIN ${1:TableName} ON ${2:Condition}$0", detail: "Redgate Snippet: INNER JOIN" },
                        { label: "lj", insertText: "LEFT JOIN ${1:TableName} ON ${2:Condition}$0", detail: "Redgate Snippet: LEFT JOIN" },
                        { label: "rj", insertText: "RIGHT JOIN ${1:TableName} ON ${2:Condition}$0", detail: "Redgate Snippet: RIGHT JOIN" },
                        { label: "fj", insertText: "FULL OUTER JOIN ${1:TableName} ON ${2:Condition}$0", detail: "Redgate Snippet: FULL OUTER JOIN" },
                        { label: "cj", insertText: "CROSS JOIN ${1:TableName}$0", detail: "Redgate Snippet: CROSS JOIN" },
                        { label: "ca", insertText: "CROSS APPLY ${1:FunctionOrSubquery}$0", detail: "Redgate Snippet: CROSS APPLY" },
                        { label: "oa", insertText: "OUTER APPLY ${1:FunctionOrSubquery}$0", detail: "Redgate Snippet: OUTER APPLY" },

                        // Control & Hints
                        { label: "wh", insertText: "WHERE $0", detail: "Redgate Snippet: WHERE" },
                        { label: "ob", insertText: "ORDER BY $0", detail: "Redgate Snippet: ORDER BY" },
                        { label: "gb", insertText: "GROUP BY $0", detail: "Redgate Snippet: GROUP BY" },
                        { label: "nolock", insertText: "WITH (NOLOCK)$0", detail: "Redgate Snippet: WITH (NOLOCK)" },
                        { label: "n", insertText: "WITH (NOLOCK)$0", detail: "Redgate Snippet: WITH (NOLOCK)" },
                        { label: "te", insertText: "TRUNCATE TABLE ${1:dbo.TableName};$0", detail: "Redgate Snippet: TRUNCATE TABLE" },

                        // Transactions & Control Flow
                        { label: "bt", insertText: "BEGIN TRANSACTION;$0", detail: "Redgate Snippet: BEGIN TRANSACTION" },
                        { label: "cmt", insertText: "COMMIT TRANSACTION;$0", detail: "Redgate Snippet: COMMIT TRANSACTION" },
                        { label: "rbt", insertText: "ROLLBACK TRANSACTION;$0", detail: "Redgate Snippet: ROLLBACK TRANSACTION" },
                        { label: "tc", insertText: "BEGIN TRY\n\t$0\nEND TRY\nBEGIN CATCH\n\tSELECT ERROR_NUMBER() AS ErrorNumber, ERROR_MESSAGE() AS ErrorMessage;\nEND CATCH", detail: "Redgate Snippet: TRY CATCH Block" },
                        { label: "iff", insertText: "IF ${1:Condition}\nBEGIN\n\t$0\nEND", detail: "Redgate Snippet: IF BEGIN END" }
                    ];

                    redgateSnippets.forEach(function(snip) {
                        var item = {
                            label: snip.label,
                            kind: monaco.languages.CompletionItemKind.Snippet,
                            insertText: snip.insertText,
                            insertTextRules: monaco.languages.CompletionItemInsertTextRule.InsertAsSnippet,
                            detail: snip.detail,
                            sortText: (sourceContext ? "8_redgate_" : "0_0_redgate_") + snip.label,
                            range: range
                        };
                        if (snip.command) {
                            item.command = snip.command;
                        }
                        suggestions.push(item);
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

                    // 5. Tables Completion (Pure table names)
                    tables.forEach(t => {
                        var shortName = t.indexOf('.') > -1 ? t.split('.')[1] : t;
                        suggestions.push({
                            label: t,
                            filterText: t + " " + shortName,
                            kind: monaco.languages.CompletionItemKind.Class,
                            insertText: t,
                            detail: objectTypes[t] || "Table",
                            sortText: (sourceContext ? "0_0_table_" : "1_table_") + shortName,
                            range: range
                        });
                    });

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

