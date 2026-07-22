const fs = require('fs');

const code = fs.readFileSync('Services/DatabaseHelper.cs', 'utf8');

const categories = {
    'Metadata': ['GetDatabasesAsync', 'SearchObjectsAcrossDatabasesAsync', 'GetTablesAsync', 'GetColumnsAsync', 'GetIndexesAsync', 'GetTriggersAsync', 'GetViewsAsync', 'GetStoredProceduresAsync', 'GetFunctionsAsync'],
    'Execution': ['ExecuteQueryAsync', 'ExecuteSessionCommandAsync', 'ExecuteBatchAsync', 'IsExecutionPlanResult'],
    'Scripting': ['GetObjectDefinitionAsync', 'GenerateTableCreateScriptAsync', 'BuildColumnDefinition', 'BuildTypeDeclaration', 'TypeSupportsCollation', 'AppendIndexScript', 'AppendDisabledIndexStatement', 'AppendIndexColumnList', 'AppendSimpleColumnList', 'AppendDataSpaceClause', 'NormalizeIndexType', 'QuoteIdentifier']
};

let lines = code.split(/\r?\n/);
let footerLines = ["    }", "}"]; 

let currentMethod = null;
let currentMethodLines = [];
let braceCount = 0;
let inMethod = false;

let coreLines = [];
let categorizedBlocks = {
    'Metadata': [],
    'Execution': [],
    'Scripting': []
};

for (let i = 0; i < lines.length; i++) {
    let line = lines[i];
    if (i >= lines.length - 3 && line.trim() === "}") continue;

    if (!inMethod) {
        let match = line.match(/^[ \t]*(?:private|public|internal|protected)[ \t]+(?:static[ \t]+)?(?:async[ \t]+)?(?:[\w<>,\?\[\]]+[ \t]+)?(\w+)\(.*?\)/);
        if (match && !line.includes(";") && !line.includes("class ") && !line.includes("record ")) {
            let methodName = match[1];
            let foundCategory = null;
            for (let cat in categories) {
                if (categories[cat].includes(methodName)) {
                    foundCategory = cat;
                    break;
                }
            }
            if (foundCategory) {
                inMethod = true;
                currentMethod = foundCategory;
                braceCount = 0;
                currentMethodLines = [];
                let j = i - 1;
                let prepend = [];
                // Look back for comments or attributes
                while (j >= 0 && (lines[j].trim().startsWith("//") || lines[j].trim().startsWith("[") || lines[j].trim() === "")) {
                    if (lines[j].trim() !== "" || prepend.length > 0) {
                        prepend.unshift(lines[j]);
                        if (coreLines.length > 0) coreLines.pop();
                    }
                    j--;
                }
                currentMethodLines.push(...prepend);
                currentMethodLines.push(line);
                if (line.includes("{")) braceCount += (line.match(/\{/g) || []).length;
                if (line.includes("}")) braceCount -= (line.match(/\}/g) || []).length;
                
                if (braceCount === 0) {
                    inMethod = false;
                    categorizedBlocks[currentMethod].push(currentMethodLines.join('\r\n'));
                    currentMethod = null;
                }
                continue;
            }
        }
        coreLines.push(line);
    } else {
        currentMethodLines.push(line);
        if (line.includes("{")) braceCount += (line.match(/\{/g) || []).length;
        if (line.includes("}")) braceCount -= (line.match(/\}/g) || []).length;
        if (braceCount === 0) {
            inMethod = false;
            categorizedBlocks[currentMethod].push(currentMethodLines.join('\r\n'));
            currentMethod = null;
        }
    }
}

// Ensure namespace and class definition are correct for the new files
let nsIndex = coreLines.findIndex(l => l.includes("namespace SSMS"));
let cleanHeader = coreLines.slice(0, nsIndex + 2); // Usings + namespace SSMS + {
cleanHeader.push("    public static partial class DatabaseHelper");
cleanHeader.push("    {");
let headerStr = cleanHeader.join('\r\n');

for (let cat in categorizedBlocks) {
    if (categorizedBlocks[cat].length > 0) {
        let content = headerStr + "\r\n\r\n" + categorizedBlocks[cat].join('\r\n\r\n') + "\r\n" + footerLines.join('\r\n');
        fs.writeFileSync(`Services/DatabaseHelper.${cat}.cs`, content);
    }
}

let coreContentStr = coreLines.join('\r\n');
coreContentStr = coreContentStr.replace("public static class DatabaseHelper", "public static partial class DatabaseHelper");
let coreContent = coreContentStr + "\r\n" + footerLines.join('\r\n');
fs.writeFileSync('Services/DatabaseHelper.cs', coreContent);

console.log("Done splitting DatabaseHelper.");
