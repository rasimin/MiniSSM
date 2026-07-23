        // Extract tables used in the query (FROM TableName / JOIN TableName)
        function getTablesInQuery(sqlText) {
            var tablesFound = [];
            var regex = /(?:FROM|JOIN|APPLY)\s+([#a-zA-Z0-9_\.\[\]]+)/gi;
            var match;
            while ((match = regex.exec(sqlText)) !== null) {
                var tableName = match[1].replace(/[\[\]]/g, ''); // Strip square brackets
                if (tablesFound.indexOf(tableName) === -1) {
                    tablesFound.push(tableName);
                }
            }
            return tablesFound;
        }

        function getActiveSqlText(model, position) {
            var fullText = model.getValue();
            var cursorOffset = model.getOffsetAt(position);
            var beforeCursor = fullText.substring(0, cursorOffset);
            var startOffset = findActiveStatementStart(beforeCursor);
            var endOffset = findActiveStatementEnd(fullText, startOffset, cursorOffset);
            return {
                text: fullText.substring(startOffset, endOffset),
                cursorOffset: cursorOffset - startOffset
            };
        }

        function findActiveStatementEnd(sqlText, startOffset, cursorOffset) {
            var depth = 0;
            var quote = null;
            var bracketDepth = 0;
            var lineStart = startOffset;

            for (var i = startOffset; i <= sqlText.length; i++) {
                var ch = i < sqlText.length ? sqlText[i] : '\n';

                if (quote) {
                    if (ch === quote) {
                        if (quote === "'" && sqlText[i + 1] === "'") {
                            i++;
                        } else {
                            quote = null;
                        }
                    }
                } else if (bracketDepth > 0) {
                    if (ch === ']') bracketDepth--;
                } else {
                    if (ch === "'" || ch === '"') {
                        quote = ch;
                    } else if (ch === '[') {
                        bracketDepth++;
                    } else if (ch === '(') {
                        depth++;
                    } else if (ch === ')') {
                        depth = Math.max(0, depth - 1);
                    } else if (ch === ';' && depth === 0 && i >= cursorOffset) {
                        return i;
                    } else if (ch === '\n' && depth === 0) {
                        var line = sqlText.substring(lineStart, i);
                        if (lineStart > cursorOffset &&
                            (/^\s*GO\s*(?:\d+)?\s*$/i.test(line) ||
                             /^\s*(?:WITH|SELECT|INSERT|UPDATE|DELETE|MERGE)\b/i.test(line))) {
                            return lineStart;
                        }
                    }
                }

                if (ch === '\n') {
                    lineStart = i + 1;
                }
            }

            return sqlText.length;
        }

        function isSelectListContext(sqlText, cursorOffset) {
            var textBeforeCursor = sqlText.substring(0, cursorOffset);
            var keywordRegex = /\b(SELECT|FROM)\b/gi;
            var match;
            var lastKeyword = '';

            while ((match = keywordRegex.exec(textBeforeCursor)) !== null) {
                lastKeyword = match[1].toUpperCase();
            }

            return lastKeyword === 'SELECT';
        }

        function findActiveStatementStart(sqlText) {
            var depth = 0;
            var quote = null;
            var bracketDepth = 0;
            var lineStart = 0;
            var lastStart = 0;

            for (var i = 0; i <= sqlText.length; i++) {
                var ch = i < sqlText.length ? sqlText[i] : '\n';

                if (quote) {
                    if (ch === quote) {
                        if (quote === "'" && sqlText[i + 1] === "'") {
                            i++;
                        } else {
                            quote = null;
                        }
                    }
                } else if (bracketDepth > 0) {
                    if (ch === ']') bracketDepth--;
                } else {
                    if (ch === "'" || ch === '"') {
                        quote = ch;
                    } else if (ch === '[') {
                        bracketDepth++;
                    } else if (ch === '(') {
                        depth++;
                    } else if (ch === ')') {
                        depth = Math.max(0, depth - 1);
                    } else if ((ch === ';' || ch === '\n') && depth === 0) {
                        var line = sqlText.substring(lineStart, i);
                        if (/^\s*GO\s*(?:\d+)?\s*$/i.test(line)) {
                            lastStart = i + 1;
                        } else if (/^\s*(?:WITH|SELECT|INSERT|UPDATE|DELETE|MERGE)\b/i.test(line)) {
                            lastStart = lineStart;
                        } else if (ch === ';') {
                            lastStart = i + 1;
                        }
                    }
                }

                if (ch === '\n') {
                    lineStart = i + 1;
                }
            }

            return lastStart;
        }

        function getQuerySources(sqlText, cursorOffset) {
            var sources = [];
            var seen = {};
            var derivedRanges = [];

            function addSource(objectName, alias, sourceStart, sourceEnd, sourceLabel) {
                if (typeof cursorOffset === "number" && sourceEnd > cursorOffset) {
                    return;
                }

                var cleanObjectName = (objectName || '').replace(/[\[\]]/g, '');
                var cleanAlias = (alias || '').replace(/[\[\]]/g, '');
                if (!cleanObjectName) return;

                var qualifier = cleanAlias || cleanObjectName;
                var key = qualifier.toLowerCase() + "|" + cleanObjectName.toLowerCase();
                if (seen[key]) return;
                seen[key] = true;

                sources.push({
                    qualifier: qualifier,
                    objectName: cleanObjectName,
                    sourceLabel: sourceLabel || cleanObjectName,
                    displayName: cleanAlias ? cleanAlias + " (" + (sourceLabel || cleanObjectName) + ")" : cleanObjectName,
                    isAlias: !!cleanAlias,
                    sourceStart: sourceStart || 0
                });
            }

            var derivedRegex = /(?:FROM|JOIN|APPLY)\s*\(\s*SELECT\s+([\s\S]*?)\s+FROM[\s\S]*?\)\s+(?:AS\s+)?(\[?[a-zA-Z0-9_]+\]?)/gi;
            var match;
            while ((match = derivedRegex.exec(sqlText)) !== null) {
                var alias = match[2].replace(/[\[\]]/g, '');
                var columns = inferSelectColumns(match[1]);
                if (columns.length > 0) {
                    tableColumns[alias] = columns;
                }
                derivedRanges.push({ start: match.index, end: derivedRegex.lastIndex });
                addSource(alias, alias, match.index, derivedRegex.lastIndex, "Derived Table");
            }

            var tableRegex = /(?:FROM|JOIN|APPLY|INSERT(?:\s+INTO)?|INTO|UPDATE|DELETE(?:\s+FROM)?|MERGE(?:\s+INTO)?)\s+([#a-zA-Z0-9_\.\[\]]+)(?:\s+(?:AS\s+)?(\[?[a-zA-Z_][a-zA-Z0-9_]*\]?))?/gi;
            var reservedAliases = {
                ON: true, WHERE: true, INNER: true, LEFT: true, RIGHT: true, FULL: true,
                CROSS: true, JOIN: true, OUTER: true, APPLY: true, GROUP: true, ORDER: true,
                HAVING: true, UNION: true, SELECT: true, TOP: true, DISTINCT: true
            };
            while ((match = tableRegex.exec(sqlText)) !== null) {
                if (derivedRanges.some(range => match.index >= range.start && match.index < range.end)) {
                    continue;
                }
                var objectName = match[1].replace(/[\[\]]/g, '');
                var aliasName = match[2] ? match[2].replace(/[\[\]]/g, '') : '';
                if (reservedAliases[aliasName.toUpperCase()]) {
                    aliasName = '';
                }
                addSource(objectName, aliasName, match.index, tableRegex.lastIndex);
            }

            sources.sort(function(a, b) { return b.sourceStart - a.sourceStart; });
            return sources;
        }

        function inferSelectColumns(selectList) {
            return splitSqlList(selectList).map(function(expression) {
                var trimmed = expression.trim();
                var aliasMatch = trimmed.match(/\bAS\s+(\[?[a-zA-Z0-9_]+\]?)\s*$/i) ||
                    trimmed.match(/\s+(\[?[a-zA-Z_][a-zA-Z0-9_]*\]?)\s*$/);
                if (aliasMatch && !/\)$/i.test(aliasMatch[1])) {
                    return aliasMatch[1].replace(/[\[\]]/g, '');
                }

                var cleanExpression = trimmed.replace(/[\[\]]/g, '');
                var parts = cleanExpression.split('.');
                var candidate = parts[parts.length - 1].match(/[a-zA-Z0-9_]+$/);
                return candidate ? candidate[0] : null;
            }).filter(Boolean);
        }

        function splitSqlList(text) {
            var parts = [];
            var current = '';
            var depth = 0;
            var quote = null;
            for (var i = 0; i < text.length; i++) {
                var ch = text[i];
                if (quote) {
                    current += ch;
                    if (ch === quote) quote = null;
                    continue;
                }
                if (ch === "'" || ch === '"') {
                    quote = ch;
                    current += ch;
                    continue;
                }
                if (ch === '(') depth++;
                if (ch === ')') depth = Math.max(0, depth - 1);
                if (ch === ',' && depth === 0) {
                    parts.push(current);
                    current = '';
                    continue;
                }
                current += ch;
            }
            if (current.trim()) parts.push(current);
            return parts;
        }

        function createColumnSuggestion(columnName, source, range, afterDot) {
            var qualifier = source && source.qualifier ? source.qualifier : '';
            var objectName = source && source.objectName ? source.objectName : '';
            var insertText = afterDot || !source || !source.isAlias ? columnName : qualifier + "." + columnName;
            var label = afterDot || !source || !source.isAlias
                ? columnName
                : qualifier + "." + columnName;
            var sourceLabel = source && source.sourceLabel ? source.sourceLabel : objectName;
            var detail = source && source.isAlias
                ? "Column (" + qualifier + " = " + sourceLabel + ")"
                : "Column (" + (sourceLabel || "table") + ")";

            return {
                label: label,
                filterText: source && source.isAlias && !afterDot ? label : columnName,
                kind: monaco.languages.CompletionItemKind.Field,
                insertText: insertText,
                detail: detail,
                sortText: "0_" + columnName,
                range: range
            };
        }

        function requestDatabaseMetadata(databaseName) {
            var key = databaseName.toLowerCase();
            if (pendingDatabaseMetadata[key] || databaseMetadata[key]) {
                return;
            }
            pendingDatabaseMetadata[key] = true;
            window.chrome.webview.postMessage({
                action: 'loadDatabaseMetadata',
                databaseName: databaseName
            });
        }

        function updateDatabaseMetadata(databaseName, metadata) {
            var key = databaseName.toLowerCase();
            databaseMetadata[key] = metadata || { columns: {}, objectTypes: {} };
            delete pendingDatabaseMetadata[key];
            if (editor) {
                editor.trigger('metadata', 'editor.action.triggerSuggest', {});
            }
        }

        function findColumns(objectName) {
            var normalized = objectName.replace(/[\[\]]/g, '');
            var columns = tableColumns[normalized];
            if (columns) {
                return columns;
            }

            for (var key in tableColumns) {
                if (key.toLowerCase().endsWith('.' + normalized.toLowerCase())) {
                    return tableColumns[key];
                }
            }

            var parts = normalized.split('.');
            if (parts.length >= 3) {
                var databaseName = parts[parts.length - 3].toLowerCase();
                var schemaObject = parts.slice(-2).join('.');
                var metadata = databaseMetadata[databaseName];
                return metadata && metadata.columns ? metadata.columns[schemaObject] : null;
            }
            return null;
        }

        function getSqlTokenAtPosition(model, position) {
            var selection = editor ? editor.getSelection() : null;
            if (selection && !selection.isEmpty() && monaco.Range.containsPosition(selection, position)) {
                var selectedToken = model.getValueInRange(selection).trim();
                if (/^[#\[\]a-zA-Z0-9_.]+$/.test(selectedToken)) {
                    return { token: selectedToken, range: selection };
                }
            }

            var line = model.getLineContent(position.lineNumber);
            var offset = position.column - 1;
            var regex = /[#\[\]a-zA-Z0-9_.]+/g;
            var match;
            while ((match = regex.exec(line)) !== null) {
                if (offset >= match.index && offset <= match.index + match[0].length) {
                    return {
                        token: match[0],
                        range: new monaco.Range(
                            position.lineNumber,
                            match.index + 1,
                            position.lineNumber,
                            match.index + match[0].length + 1)
                    };
                }
            }
            return null;
        }

        function findCaseInsensitiveProperty(object, propertyName) {
            if (!object) return null;
            var key = Object.keys(object).find(name => name.toLowerCase() === propertyName.toLowerCase());
            return key ? { key: key, value: object[key] } : null;
        }

        function findColumnHoverInfo(token, sqlText) {
            var parts = token.split('.');
            var columnName = parts[parts.length - 1];
            var objectCandidates = [];

            if (parts.length > 1) {
                var qualifier = parts.slice(0, -1).join('.');
                var aliasObject = getTableForAlias(qualifier, sqlText);
                objectCandidates.push(aliasObject || qualifier);
            } else {
                objectCandidates = getTablesInQuery(sqlText);
            }

            for (var i = 0; i < objectCandidates.length; i++) {
                var objectName = objectCandidates[i];
                var detailsEntry = findCaseInsensitiveProperty(columnDetails, objectName);
                if (!detailsEntry) {
                    detailsEntry = Object.keys(columnDetails)
                        .filter(key => key.toLowerCase().endsWith('.' + objectName.toLowerCase()))
                        .map(key => ({ key: key, value: columnDetails[key] }))[0];
                }
                if (!detailsEntry) continue;

                var columnEntry = findCaseInsensitiveProperty(detailsEntry.value, columnName);
                if (columnEntry) {
                    return {
                        name: detailsEntry.key + "." + columnEntry.key,
                        info: columnEntry.value
                    };
                }
            }
            return null;
        }

        function findObjectHoverInfo(token) {
            var objectEntry = findCaseInsensitiveProperty(objectTypes, token);
            if (!objectEntry) {
                objectEntry = Object.keys(objectTypes)
                    .filter(key => key.toLowerCase().endsWith('.' + token.toLowerCase()))
                    .map(key => ({ key: key, value: objectTypes[key] }))[0];
            }
            if (objectEntry) {
                var canonicalName = objectEntry.key.indexOf('.') > -1
                    ? objectEntry.key
                    : Object.keys(objectTypes).find(key =>
                        key.indexOf('.') > -1 && key.toLowerCase().endsWith('.' + objectEntry.key.toLowerCase())) || objectEntry.key;
                return {
                    name: canonicalName,
                    type: objectEntry.value,
                    label: objectEntry.value
                };
            }

            var routineGroups = [
                { items: storedProcedures, type: "StoredProcedure", label: "Stored Procedure" },
                { items: scalarFunctions, type: "Function", label: "Scalar Function" },
                { items: tableFunctions, type: "Function", label: "Table-valued Function" }
            ];
            for (var i = 0; i < routineGroups.length; i++) {
                var routineName = routineGroups[i].items.find(name =>
                    name.toLowerCase() === token.toLowerCase() ||
                    name.toLowerCase().endsWith('.' + token.toLowerCase()));
                if (routineName) {
                    return {
                        name: routineName,
                        type: routineGroups[i].type,
                        label: routineGroups[i].label
                    };
                }
            }
            return null;
        }

        // Resolve Table Alias by searching backwards in sql text
        function getTableForAlias(alias, sqlText, cursorOffset) {
            var escapedAlias = alias.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
            var regex = new RegExp("(?:FROM|JOIN|APPLY)\\s+([#a-zA-Z0-9_\\.\\[\\]]+)\\s+(?:AS\\s+)?" + escapedAlias + "\\b", "gi");
            var match;
            var bestMatch = null;
            var bestStart = -1;

            while ((match = regex.exec(sqlText)) !== null) {
                var matchStart = match.index;
                var matchEnd = regex.lastIndex;

                if (typeof cursorOffset === "number" && matchEnd > cursorOffset) {
                    continue;
                }

                if (matchStart > bestStart) {
                    bestStart = matchStart;
                    bestMatch = match[1];
                }
            }

            return bestMatch ? bestMatch.replace(/[\[\]]/g, '') : null;
        }

        // Extract CTEs and temporary tables declared in the current editor text.
        function getLocalObjects(sqlText) {
            var objects = {};
            var cteRegex = /(?:\bWITH|,)\s*(\[?[a-zA-Z0-9_]+\]?)\s*(?:\(([^)]*)\))?\s+AS\s*\(/gi;
            var match;
            while ((match = cteRegex.exec(sqlText)) !== null) {
                var cteName = match[1].replace(/[\[\]]/g, '');
                var cteColumns = match[2]
                    ? match[2].split(',').map(c => c.trim().replace(/[\[\]]/g, '')).filter(Boolean)
                    : [];
                objects[cteName] = { type: "CTE", columns: cteColumns };
                tableColumns[cteName] = cteColumns;
            }

            var inferredCteRegex = /(?:\bWITH|,)\s*(\[?[a-zA-Z0-9_]+\]?)\s+AS\s*\(\s*SELECT\s+([\s\S]*?)\s+FROM\b/gi;
            while ((match = inferredCteRegex.exec(sqlText)) !== null) {
                var inferredName = match[1].replace(/[\[\]]/g, '');
                var inferredColumns = match[2].split(',').map(expression => {
                    var aliasMatch = expression.trim().match(/\bAS\s+(\[?[a-zA-Z0-9_]+\]?)\s*$/i);
                    if (aliasMatch) {
                        return aliasMatch[1].replace(/[\[\]]/g, '');
                    }
                    var cleanExpression = expression.trim().replace(/[\[\]]/g, '');
                    var parts = cleanExpression.split('.');
                    var candidate = parts[parts.length - 1].match(/[a-zA-Z0-9_]+$/);
                    return candidate ? candidate[0] : null;
                }).filter(Boolean);
                if (objects[inferredName] && objects[inferredName].columns.length === 0) {
                    objects[inferredName].columns = inferredColumns;
                    tableColumns[inferredName] = inferredColumns;
                }
            }

            var tempRegex = /\bCREATE\s+TABLE\s+(#(?:#)?[a-zA-Z0-9_]+)\s*\(([\s\S]*?)\)/gi;
            while ((match = tempRegex.exec(sqlText)) !== null) {
                var tempColumns = match[2].split(',').map(definition => {
                    var columnMatch = definition.trim().match(/^(\[?[a-zA-Z0-9_]+\]?)/);
                    return columnMatch ? columnMatch[1].replace(/[\[\]]/g, '') : null;
                }).filter(Boolean);
                objects[match[1]] = { type: "Temporary Table", columns: tempColumns };
                tableColumns[match[1]] = tempColumns;
            }

            return objects;
        }

        function findTableColumnDetails(objectName) {
            if (!objectName) return null;
            var normalized = objectName.replace(/[\[\]]/g, '');
            var details = columnDetails[normalized];
            if (details) {
                return details;
            }

            for (var key in columnDetails) {
                if (key.toLowerCase() === normalized.toLowerCase() || key.toLowerCase().endsWith('.' + normalized.toLowerCase())) {
                    return columnDetails[key];
                }
            }
            return null;
        }

        function getFullTableName(tableName) {
            if (!tableName) return tableName;
            var normalized = tableName.replace(/[\[\]]/g, '');
            for (var key in objectTypes) {
                if (key.toLowerCase() === normalized.toLowerCase() || key.toLowerCase().endsWith('.' + normalized.toLowerCase())) {
                    var parts = key.split('.');
                    return parts.map(p => '[' + p + ']').join('.');
                }
            }
            return normalized.indexOf('.') > -1
                ? normalized.split('.').map(p => '[' + p + ']').join('.')
                : '[dbo].[' + normalized + ']';
        }

        function getDefaultSampleValue(colName, dataType) {
            var dt = (dataType || '').toLowerCase();
            if (dt.indexOf('int') > -1 || dt.indexOf('bit') > -1 || dt.indexOf('tinyint') > -1 || dt.indexOf('smallint') > -1) {
                return "0";
            }
            if (dt.indexOf('decimal') > -1 || dt.indexOf('numeric') > -1 || dt.indexOf('float') > -1 || dt.indexOf('money') > -1 || dt.indexOf('real') > -1) {
                return "0.0";
            }
            if (dt.indexOf('datetime') > -1 || dt.indexOf('date') > -1 || dt.indexOf('time') > -1) {
                return "GETDATE()";
            }
            if (dt.indexOf('uniqueidentifier') > -1) {
                return "NEWID()";
            }
            return "'" + colName + "'";
        }

        function generateInsertSnippet(tableName) {
            var details = findTableColumnDetails(tableName);
            var cols = findColumns(tableName);
            if (!cols || cols.length === 0) return null;

            var insertCols = [];
            var valList = [];
            var tabIndex = 1;

            cols.forEach(function(col) {
                var info = details ? details[col] : null;
                var dataType = info ? info.dataType : '';
                var isIdentity = info ? info.isIdentity : false;

                if (isIdentity) return;

                insertCols.push("    [" + col + "]");

                var defaultVal = getDefaultSampleValue(col, dataType);
                var remark = dataType ? " /* " + dataType + " */" : "";
                valList.push("    ${" + tabIndex + ":" + defaultVal + "}" + remark);
                tabIndex++;
            });

            if (insertCols.length === 0) return null;

            var fullTableName = getFullTableName(tableName);
            return "INSERT INTO " + fullTableName + " (\n" +
                insertCols.join(",\n") + "\n" +
                ")\nVALUES (\n" +
                valList.join(",\n") + "\n);$0";
        }

        function generateUpdateSnippet(tableName) {
            var details = findTableColumnDetails(tableName);
            var cols = findColumns(tableName);
            if (!cols || cols.length === 0) return null;

            var setLines = [];
            var whereCols = [];
            var tabIndex = 1;

            cols.forEach(function(col) {
                var info = details ? details[col] : null;
                var dataType = info ? info.dataType : '';
                var isIdentity = info ? info.isIdentity : false;
                var isPrimaryKey = info ? info.isPrimaryKey : false;
                var colLower = col.toLowerCase();
                var isAuditCreated = colLower === 'createddate' || colLower === 'createdat' || colLower === 'createdby' || colLower === 'created_date' || colLower === 'created_at' || colLower === 'created_by';

                var defaultVal = getDefaultSampleValue(col, dataType);
                var remark = dataType ? " /* " + dataType + " */" : "";

                if (isPrimaryKey || isIdentity) {
                    whereCols.push({ col: col, val: defaultVal, dataType: dataType });
                    setLines.push("    -- [" + col + "] = " + defaultVal + remark + " /* (Primary Key/Identity - tidak perlu update) */");
                } else if (isAuditCreated) {
                    setLines.push("    -- [" + col + "] = " + defaultVal + remark + " /* (Created Audit - tidak perlu update) */");
                } else {
                    setLines.push("    [" + col + "] = ${" + tabIndex + ":" + defaultVal + "}" + remark);
                    tabIndex++;
                }
            });

            if (setLines.length === 0) return null;

            var fullTableName = getFullTableName(tableName);
            var whereClause = "";
            if (whereCols.length > 0) {
                whereClause = "\nWHERE " + whereCols.map(function(w, idx) {
                    return "[" + w.col + "] = ${" + (tabIndex + idx) + ":" + w.val + "}";
                }).join(" AND ");
            } else if (cols.length > 0) {
                whereClause = "\nWHERE [" + cols[0] + "] = ${" + tabIndex + ":'val'}";
            }

            return "UPDATE " + fullTableName + "\nSET\n" +
                setLines.join(",\n") +
                whereClause + ";$0";
        }
