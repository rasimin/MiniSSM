        function registerSqlHoverProvider() {
            monaco.editor.registerCommand('minissms.viewObject', function (_, objectName, objectType) {
                window.chrome.webview.postMessage({
                    action: 'viewObjectDefinition',
                    objectName: objectName,
                    objectType: objectType
                });
            });

            monaco.languages.registerHoverProvider('sql', {
                provideHover: function (model, position) {
                    var tokenInfo = getSqlTokenAtPosition(model, position);
                    if (!tokenInfo || !tokenInfo.token) {
                        return null;
                    }

                    var token = tokenInfo.token.replace(/[\[\]]/g, '');
                    var columnInfo = findColumnHoverInfo(token, model.getValue());
                    if (columnInfo) {
                        var isNullable = columnInfo.info.isNullable ?? columnInfo.info.IsNullable;
                        var isIdentity = columnInfo.info.isIdentity ?? columnInfo.info.IsIdentity;
                        var isPrimaryKey = columnInfo.info.isPrimaryKey ?? columnInfo.info.IsPrimaryKey;
                        var dataType = columnInfo.info.dataType || columnInfo.info.DataType;
                        var flags = [];
                        flags.push(isNullable ? "NULL" : "NOT NULL");
                        if (isIdentity) flags.push("IDENTITY");
                        if (isPrimaryKey) flags.push("PRIMARY KEY");
                        return {
                            range: tokenInfo.range,
                            contents: [{
                                value: "**Column:** `" + columnInfo.name + "`  \n" +
                                    "**Type:** `" + dataType + "`  \n" +
                                    flags.join(" Â· ")
                            }]
                        };
                    }

                    var objectInfo = findObjectHoverInfo(token);
                    if (!objectInfo) {
                        return null;
                    }

                    var commandArgs = encodeURIComponent(JSON.stringify([
                        objectInfo.name,
                        objectInfo.type
                    ]));
                    return {
                        range: tokenInfo.range,
                        contents: [{
                            value: "**" + objectInfo.label + ":** `" + objectInfo.name + "`  \n\n" +
                                "[View Schema / Definition](command:minissms.viewObject?" + commandArgs + ")",
                            isTrusted: true
                        }]
                    };
                }
            });
        }

