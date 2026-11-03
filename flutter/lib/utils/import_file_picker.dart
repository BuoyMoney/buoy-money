import 'package:file_picker/file_picker.dart';

import '../models/import_models.dart';

/// The file types the import screen accepts: bank files, spreadsheets exported as CSV, and the
/// zips that Actual Budget and YNAB produce.
const importAllowedExtensions = ['csv', 'tsv', 'txt', 'ofx', 'qfx', 'zip'];

/// Biggest file we will read into memory before uploading (matches the server limit).
const importMaxBytes = 25 * 1024 * 1024;

/// Opens the system file picker and reads the chosen files fully into memory, so the upload
/// works on web (no file path) and on mobile alike. Returns an empty list when cancelled.
/// file_picker 13 has no allowMultiple flag: pickFiles is always multi-select (pickFile is the
/// single one), so YNAB's Register and Plan pair can be chosen together.
Future<List<ImportPickedFile>> pickImportFiles() async {
  final picked = await FilePicker.pickFiles(
    type: FileType.custom,
    allowedExtensions: importAllowedExtensions,
  );
  final files = <ImportPickedFile>[];
  for (final file in picked) {
    final length = await file.length();
    if (length != null && length > importMaxBytes) throw ImportFileTooLarge(file.name);
    files.add(ImportPickedFile(name: file.name, bytes: await file.readAsBytes()));
  }
  return files;
}
