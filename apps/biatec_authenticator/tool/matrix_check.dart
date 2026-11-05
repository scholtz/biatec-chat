// Checks docs/AUTHENTICATOR-SECURITY.md: every MASVS v2.0 control is in the matrix once, with a valid status, and the evidence is real.
//
//   dart run tool/matrix_check.dart [path to AUTHENTICATOR-SECURITY.md]
//
// Rules: a `met` row links at least one file under apps/ or .github/ (code, a test or a workflow) that exists; a `partly` row links one that
// exists too and says what is missing (a task id or an owner); an `open` row names a task (T-123, and its file exists) or an owner; a
// `not applicable` row says why. Every local link must point at an existing file or folder. Links are relative to the document.
import 'dart:io';

/// The 24 controls of OWASP MASVS v2.0 (Level 1 and Level 2 share the control list).
const masvsControls = [
  'MASVS-STORAGE-1',
  'MASVS-STORAGE-2',
  'MASVS-CRYPTO-1',
  'MASVS-CRYPTO-2',
  'MASVS-AUTH-1',
  'MASVS-AUTH-2',
  'MASVS-AUTH-3',
  'MASVS-NETWORK-1',
  'MASVS-NETWORK-2',
  'MASVS-PLATFORM-1',
  'MASVS-PLATFORM-2',
  'MASVS-PLATFORM-3',
  'MASVS-CODE-1',
  'MASVS-CODE-2',
  'MASVS-CODE-3',
  'MASVS-CODE-4',
  'MASVS-RESILIENCE-1',
  'MASVS-RESILIENCE-2',
  'MASVS-RESILIENCE-3',
  'MASVS-RESILIENCE-4',
  'MASVS-PRIVACY-1',
  'MASVS-PRIVACY-2',
  'MASVS-PRIVACY-3',
  'MASVS-PRIVACY-4',
];

const statuses = {'met', 'partly', 'open', 'not applicable'};

class MatrixRow {
  MatrixRow(this.control, this.status, this.evidence, this.notes);

  final String control;
  final String status;
  final String evidence;
  final String notes;
}

/// Splits a table row into cells: on `|` that is not escaped as `\|`, with or without the outer pipes.
List<String> splitRow(String line) {
  final cells = <String>[];
  final cell = StringBuffer();
  for (var i = 0; i < line.length; i++) {
    final c = line[i];
    if (c == r'\' && i + 1 < line.length && line[i + 1] == '|') {
      cell.write('|');
      i++;
    } else if (c == '|') {
      cells.add(cell.toString().trim());
      cell.clear();
    } else {
      cell.write(c);
    }
  }
  cells.add(cell.toString().trim());
  // The line began with a pipe (an empty first cell) and usually ends with one (an empty last cell).
  if (cells.isNotEmpty && cells.first.isEmpty) cells.removeAt(0);
  if (cells.isNotEmpty && cells.last.isEmpty) cells.removeLast();
  return cells;
}

/// The rows of the matrix table: `| MASVS-X-n | requirement | status | evidence | notes |`.
List<MatrixRow> parseMatrix(String markdown) {
  final rows = <MatrixRow>[];
  for (final line in markdown.split('\n')) {
    final t = line.trim();
    if (!t.startsWith('| MASVS-')) continue;
    final cells = splitRow(t);
    if (cells.length < 5) {
      rows.add(MatrixRow(cells.first, '', '', ''));
      continue;
    }
    rows.add(MatrixRow(cells[0], cells[2].toLowerCase(), cells[3], cells[4]));
  }
  return rows;
}

final _link = RegExp(r'\[[^\]]*\]\(([^)#\s]+)(?:#[^)]*)?\)');
final _task = RegExp(r'\bT-\d{3}\b');
final _scheme = RegExp(r'^[A-Za-z][A-Za-z0-9+.-]*:');

bool _external(String link) => _scheme.hasMatch(link);

/// A link that is evidence of an implementation or a test: a file in the app or in CI, not another document.
bool _isImplementation(String link) {
  final path = link.replaceAll('\\', '/');
  return path.contains('apps/') || path.contains('.github/');
}

/// Problems with the matrix in [markdown], with links resolved against [docsDir] and tasks against [tasksDir]. Empty when it is sound.
List<String> checkMatrix(String markdown,
    {required String docsDir, required String tasksDir}) {
  final problems = <String>[];
  final rows = parseMatrix(markdown);
  final taskIds = <String>{};
  if (Directory(tasksDir).existsSync()) {
    for (final f in Directory(tasksDir).listSync()) {
      final name = f.path.replaceAll('\\', '/').split('/').last;
      if (RegExp(r'^T-\d{3}-').hasMatch(name)) {
        taskIds.add(name.substring(0, 5));
      }
    }
  }
  final seen = <String>{};
  for (final row in rows) {
    if (!seen.add(row.control)) problems.add('${row.control}: appears twice');
    if (!masvsControls.contains(row.control)) {
      problems.add('${row.control}: not a MASVS v2.0 control');
    }
    if (!statuses.contains(row.status)) {
      problems.add(
          '${row.control}: status "${row.status}" is not one of ${statuses.join(', ')}');
      continue;
    }
    final links =
        _link.allMatches(row.evidence).map((m) => m.group(1)!).toList();
    final local = links.where((l) => !_external(l)).toList();
    for (final l in local) {
      if (!FileSystemEntity.isFileSync('$docsDir/$l') &&
          !FileSystemEntity.isDirectorySync('$docsDir/$l')) {
        problems.add('${row.control}: evidence link "$l" does not exist');
      }
    }
    final implementation = local
        .where(_isImplementation)
        .where((l) => FileSystemEntity.isFileSync('$docsDir/$l'));
    final text = '${row.evidence} ${row.notes}';
    switch (row.status) {
      case 'met':
        if (implementation.isEmpty) {
          problems.add('${row.control}: met, but no evidence link to a file '
              'under apps/ or .github/ (code, a test or a workflow)');
        }
      case 'partly':
        if (implementation.isEmpty) {
          problems.add('${row.control}: partly, but no evidence link to a '
              'file under apps/ or .github/');
        }
        if (!_task.hasMatch(text) && !text.contains('owner:')) {
          problems.add('${row.control}: partly, but the missing part has no '
              'task id or owner');
        }
      case 'open':
        if (!_task.hasMatch(text) && !text.contains('owner:')) {
          problems.add('${row.control}: open, with no task id or owner');
        }
      case 'not applicable':
        if (row.notes.length < 12 && row.evidence.length < 12) {
          problems.add('${row.control}: not applicable, with no reason');
        }
    }
    for (final id in _task.allMatches(text).map((m) => m.group(0)!)) {
      if (!taskIds.contains(id)) {
        problems.add('${row.control}: names $id, which has no task file');
      }
    }
  }
  for (final c in masvsControls) {
    if (!seen.contains(c)) problems.add('$c: missing from the matrix');
  }
  return problems;
}

void main(List<String> args) {
  final path =
      args.isNotEmpty ? args.first : '../../docs/AUTHENTICATOR-SECURITY.md';
  final file = File(path);
  if (!file.existsSync()) {
    stderr.writeln('matrix_check: $path does not exist');
    exit(2);
  }
  final docsDir = file.parent.path;
  final tasksDir = '$docsDir/../kanban/tasks';
  if (!Directory(tasksDir).existsSync()) {
    stderr.writeln('matrix_check: $tasksDir does not exist (the document '
        'must sit in the repository docs folder)');
    exit(2);
  }
  final problems = checkMatrix(file.readAsStringSync(),
      docsDir: docsDir, tasksDir: tasksDir);
  if (problems.isEmpty) {
    stdout.writeln('matrix_check: ${masvsControls.length} controls, all with '
        'a status and real evidence.');
    return;
  }
  for (final p in problems) {
    stderr.writeln('matrix_check: $p');
  }
  exit(1);
}
