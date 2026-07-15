        function formatSql() {
            if (!editor) return;

            var model = editor.getModel();
            var selection = editor.getSelection();
            var hasSelection = !selection.isEmpty();
            var range = hasSelection ? selection : model.getFullModelRange();
            var source = model.getValueInRange(range);
            var formatted = formatSqlText(source);
            if (formatted === source) return;

            editor.pushUndoStop();
            editor.executeEdits('format-sql', [{ range: range, text: formatted, forceMoveMarkers: true }]);
            editor.pushUndoStop();
            editor.focus();
        }

        function formatSqlText(source) {
            var protectedSegments = [];
            var protectedSql = source.replace(/(^[ \t]*GO(?:[ \t]+\d+)?[ \t]*(?:--[^\r\n]*)?$|--[^\r\n]*|\/\*[\s\S]*?\*\/|'(?:''|[^'])*'|\[(?:\]\]|[^\]])*\]|"(?:""|[^"])*")/gmi, function(segment) {
                var token = '__MINISQL_' + protectedSegments.length + '__';
                protectedSegments.push(segment);
                return token;
            });

            protectedSql = protectedSql.replace(/[ \t]+/g, ' ').replace(/\s*\r?\n\s*/g, ' ');
            protectedSegments.forEach(function(segment, index) {
                if (/^\s*(--|GO\b)/i.test(segment)) {
                    var token = '__MINISQL_' + index + '__';
                    protectedSql = protectedSql.replace(token, token + '\n');
                }
            });

            var keywords = [
                'select', 'from', 'where', 'group by', 'order by', 'having', 'union all', 'union',
                'inner join', 'left outer join', 'right outer join', 'full outer join',
                'left join', 'right join', 'full join', 'cross join', 'join', 'on',
                'insert into', 'values', 'update', 'delete from', 'set',
                'create', 'alter', 'drop', 'as', 'begin', 'end', 'case', 'when', 'then', 'else',
                'and', 'or', 'not', 'null', 'is', 'in', 'exists', 'distinct', 'top', 'with'
            ];
            keywords.sort(function(a, b) { return b.length - a.length; });
            keywords.forEach(function(keyword) {
                var pattern = new RegExp('\\b' + keyword.replace(/ /g, '\\s+') + '\\b', 'gi');
                protectedSql = protectedSql.replace(pattern, keyword.toUpperCase());
            });

            var newlineClauses = [
                'SELECT', 'FROM', 'WHERE', 'GROUP BY', 'ORDER BY', 'HAVING', 'UNION ALL', 'UNION',
                'INNER JOIN', 'LEFT OUTER JOIN', 'RIGHT OUTER JOIN', 'FULL OUTER JOIN',
                'LEFT JOIN', 'RIGHT JOIN', 'FULL JOIN', 'CROSS JOIN', 'JOIN',
                'INSERT INTO', 'VALUES', 'UPDATE', 'DELETE FROM', 'SET', 'BEGIN', 'END'
            ];
            newlineClauses.sort(function(a, b) { return b.length - a.length; });
            newlineClauses.forEach(function(clause) {
                var pattern = new RegExp('\\s+(' + clause.replace(/ /g, '\\s+') + ')\\b', 'g');
                protectedSql = protectedSql.replace(pattern, '\n$1');
            });
            protectedSql = protectedSql.replace(/\s+(ON)\b/g, '\n    ON');
            protectedSql = protectedSql.replace(/\s+(AND|OR)\b/g, '\n    $1');

            var indent = 0;
            var lines = protectedSql.split(/\r?\n/).map(function(line) {
                line = line.trim();
                if (!line) return '';
                if (/^(END\b)/.test(line)) indent = Math.max(0, indent - 1);
                var formattedLine = new Array(indent + 1).join('    ') + line;
                if (/^(BEGIN\b)/.test(line)) indent++;
                return formattedLine;
            }).filter(function(line) { return line.length > 0; });

            var result = lines.join('\n');
            protectedSegments.forEach(function(segment, index) {
                result = result.replace('__MINISQL_' + index + '__', segment);
            });
            return result.trim();
        }

