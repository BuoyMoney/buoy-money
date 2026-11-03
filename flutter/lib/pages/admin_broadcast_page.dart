import 'dart:math';

import 'package:dio/dio.dart';
import 'package:flutter/material.dart';
import 'package:flutter_markdown/flutter_markdown.dart';
import 'package:markdown/markdown.dart' as md;

import '../l10n/generated/app_localizations.dart';
import '../services/broadcast_service.dart';

final _emailMarkdown = md.ExtensionSet(
  [...md.ExtensionSet.commonMark.blockSyntaxes, const md.TableSyntax()],
  [...md.ExtensionSet.commonMark.inlineSyntaxes],
);

/// Write, preview and send an email to users. Admin and hosted edition only (the nav and the router
/// gate it; the server enforces both again, plus phishing-resistant MFA).
///
/// Two steps: "Preview recipients" runs the server's dry run and shows the count; "Send now" stays
/// disabled until that exact count is typed back. Changing anything after a preview throws the
/// preview away, because the count and the email may no longer match what was checked.
class AdminBroadcastPage extends StatefulWidget {
  const AdminBroadcastPage({super.key, this.service});

  /// Test seam.
  final BroadcastService? service;

  @override
  State<AdminBroadcastPage> createState() => _AdminBroadcastPageState();
}

class _AdminBroadcastPageState extends State<AdminBroadcastPage> {
  late final BroadcastService _service = widget.service ?? broadcastService;
  final _subject = TextEditingController();
  final _body = TextEditingController();
  final _confirm = TextEditingController();

  String _audience = 'all';
  String _category = 'announcement';
  BroadcastResult? _preview;
  // One key per successful preview: a send that fails after the server committed can be retried
  // without queueing a second broadcast. Cleared whenever the preview is dropped.
  String? _idempotencyKey;
  // Text the current preview was run against; only a real text change invalidates it.
  String? _previewedSubject;
  String? _previewedBody;
  BroadcastResult? _sent;
  bool _busy = false;
  bool _edited = false;
  String? _error;

  @override
  void initState() {
    super.initState();
    _subject.addListener(_onEdited);
    _body.addListener(_onEdited);
  }

  @override
  void dispose() {
    _subject.dispose();
    _body.dispose();
    _confirm.dispose();
    super.dispose();
  }

  void _onEdited() {
    // Rebuilds the live preview; also drops a stale dry run.
    setState(() {
      // Controllers also notify on cursor and selection moves; those must not drop the dry run.
      final changed = _subject.text.trim() != _previewedSubject ||
          _body.text != _previewedBody;
      if (_preview != null && changed) {
        _preview = null;
        _idempotencyKey = null;
        _confirm.clear();
        _edited = true;
      }
    });
  }

  void _setAudience(String v) => setState(() {
        _audience = v;
        _dropPreview();
      });

  void _setCategory(String v) => setState(() {
        _category = v;
        _dropPreview();
      });

  void _dropPreview() {
    if (_preview != null) {
      _preview = null;
      _idempotencyKey = null;
      _confirm.clear();
      _edited = true;
    }
  }

  static String _newKey() {
    final r = Random.secure();
    return List.generate(
        16, (_) => r.nextInt(256).toRadixString(16).padLeft(2, '0')).join();
  }

  String _errorText(Object e) {
    final l10n = AppLocalizations.of(context);
    if (e is DioException) {
      final data = e.response?.data;
      final message = data is Map && data['message'] is String
          ? data['message'] as String
          : null;
      if (e.response?.statusCode == 403) {
        // The MFA 403 carries a message; a role 403 (stale token) has none, so do not blame MFA.
        if (message != null && message.contains('MFA')) {
          return l10n.broadcastErrorMfa;
        }
        return message ?? l10n.broadcastErrorGeneric;
      }
      if (message != null) return message;
    }
    return l10n.broadcastErrorGeneric;
  }

  Future<void> _runPreview() async {
    final l10n = AppLocalizations.of(context);
    if (_subject.text.trim().isEmpty || _body.text.trim().isEmpty) {
      setState(() => _error = l10n.broadcastFieldsRequired);
      return;
    }
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final result = await _service.preview(
        subject: _subject.text.trim(),
        bodyMarkdown: _body.text,
        audience: _audience,
        category: _category,
      );
      if (!mounted) return;
      setState(() {
        _preview = result;
        _previewedSubject = _subject.text.trim();
        _previewedBody = _body.text;
        _idempotencyKey = _newKey();
        _edited = false;
        _confirm.clear();
        _busy = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = _errorText(e);
        _busy = false;
      });
    }
  }

  bool get _confirmed =>
      _preview != null &&
      _confirm.text.trim() == _preview!.recipientCount.toString();

  Future<void> _send() async {
    final preview = _preview;
    if (preview == null || !_confirmed) return;
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      final result = await _service.send(
        subject: _subject.text.trim(),
        bodyMarkdown: _body.text,
        audience: _audience,
        category: _category,
        confirmRecipientCount: preview.recipientCount,
        idempotencyKey: _idempotencyKey,
      );
      if (!mounted) return;
      setState(() {
        _sent = result;
        _busy = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _error = _errorText(e);
        _busy = false;
      });
    }
  }

  void _reset() {
    setState(() {
      _subject.clear();
      _body.clear();
      _confirm.clear();
      _preview = null;
      _idempotencyKey = null;
      _sent = null;
      _edited = false;
      _error = null;
    });
  }

  @override
  Widget build(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    return Scaffold(
      appBar: AppBar(title: Text(l10n.broadcastTitle)),
      body: SafeArea(
        child: Align(
          alignment: Alignment.topCenter,
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 720),
            child: _sent != null
                ? _buildSent(context, _sent!)
                : _buildForm(context),
          ),
        ),
      ),
    );
  }

  Widget _buildSent(BuildContext context, BroadcastResult sent) {
    final l10n = AppLocalizations.of(context);
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Card(
          child: Padding(
            padding: const EdgeInsets.all(16),
            child: Text(l10n.broadcastSent(sent.recipientCount),
                style: Theme.of(context).textTheme.titleMedium),
          ),
        ),
        const SizedBox(height: 16),
        OutlinedButton(onPressed: _reset, child: Text(l10n.broadcastNewOne)),
      ],
    );
  }

  Widget _buildForm(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final preview = _preview;
    return ListView(
      padding: const EdgeInsets.all(16),
      children: [
        Text(l10n.broadcastIntro, style: theme.textTheme.bodyMedium),
        const SizedBox(height: 16),
        TextField(
          controller: _subject,
          enabled: !_busy,
          maxLength: 300,
          decoration: InputDecoration(
              labelText: l10n.broadcastSubject,
              border: const OutlineInputBorder()),
        ),
        const SizedBox(height: 8),
        TextField(
          controller: _body,
          enabled: !_busy,
          minLines: 6,
          maxLines: 14,
          decoration: InputDecoration(
            labelText: l10n.broadcastBody,
            hintText: l10n.broadcastBodyHint,
            alignLabelWithHint: true,
            border: const OutlineInputBorder(),
          ),
        ),
        const SizedBox(height: 16),
        Text(l10n.broadcastAudience, style: theme.textTheme.labelLarge),
        const SizedBox(height: 6),
        SegmentedButton<String>(
          segments: [
            ButtonSegment(value: 'all', label: Text(l10n.broadcastAudienceAll)),
            ButtonSegment(
                value: 'active', label: Text(l10n.broadcastAudienceActive)),
          ],
          selected: {_audience},
          onSelectionChanged: _busy ? null : (s) => _setAudience(s.first),
        ),
        const SizedBox(height: 16),
        Text(l10n.broadcastCategory, style: theme.textTheme.labelLarge),
        const SizedBox(height: 6),
        SegmentedButton<String>(
          segments: [
            ButtonSegment(
                value: 'announcement',
                label: Text(l10n.broadcastCategoryAnnouncement)),
            ButtonSegment(
                value: 'incident', label: Text(l10n.broadcastCategoryIncident)),
          ],
          selected: {_category},
          onSelectionChanged: _busy ? null : (s) => _setCategory(s.first),
        ),
        const SizedBox(height: 4),
        Text(
          l10n.broadcastCategoryHelp,
          style: theme.textTheme.bodySmall
              ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
        ),
        const SizedBox(height: 16),
        _buildLivePreview(context),
        const SizedBox(height: 16),
        if (_error != null) ...[
          _Banner(text: _error!, isError: true),
          const SizedBox(height: 12),
        ],
        if (_edited && preview == null) ...[
          _Banner(text: l10n.broadcastEditedNote, isError: false),
          const SizedBox(height: 12),
        ],
        FilledButton(
          onPressed: _busy ? null : _runPreview,
          child: Text(_busy && preview == null
              ? l10n.broadcastPreviewing
              : l10n.broadcastPreviewButton),
        ),
        if (preview != null) ...[
          const SizedBox(height: 16),
          Card(
            child: Padding(
              padding: const EdgeInsets.all(16),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(l10n.broadcastRecipients(preview.recipientCount),
                      style: theme.textTheme.titleMedium),
                  if (preview.skippedCount > 0) ...[
                    const SizedBox(height: 4),
                    Text(
                      l10n.broadcastSkipped(preview.skippedCount),
                      style: theme.textTheme.bodySmall
                          ?.copyWith(color: theme.colorScheme.onSurfaceVariant),
                    ),
                  ],
                  const SizedBox(height: 16),
                  TextField(
                    controller: _confirm,
                    enabled: !_busy,
                    keyboardType: TextInputType.number,
                    onChanged: (_) => setState(() {}),
                    decoration: InputDecoration(
                      labelText:
                          l10n.broadcastConfirmLabel(preview.recipientCount),
                      border: const OutlineInputBorder(),
                    ),
                  ),
                  const SizedBox(height: 12),
                  FilledButton(
                    style: FilledButton.styleFrom(
                        backgroundColor: theme.colorScheme.error),
                    onPressed: _busy || !_confirmed ? null : _send,
                    child: Text(
                        _busy ? l10n.broadcastSending : l10n.broadcastSend),
                  ),
                ],
              ),
            ),
          ),
        ],
      ],
    );
  }

  /// The email as the reader will see it: subject as a heading over the Markdown body. Uses
  /// CommonMark plus pipe tables, exactly what the server pipeline renders (no strikethrough,
  /// bare-URL autolinks or task lists).
  Widget _buildLivePreview(BuildContext context) {
    final l10n = AppLocalizations.of(context);
    final theme = Theme.of(context);
    final subject = _subject.text.trim();
    return Container(
      padding: const EdgeInsets.all(16),
      decoration: BoxDecoration(
        border: Border.all(color: theme.colorScheme.outlineVariant),
        borderRadius: BorderRadius.circular(8),
      ),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(l10n.broadcastLivePreview, style: theme.textTheme.labelMedium),
          const SizedBox(height: 8),
          if (subject.isNotEmpty)
            Text(subject, style: theme.textTheme.titleLarge),
          if (subject.isNotEmpty) const SizedBox(height: 8),
          MarkdownBody(
            data: _body.text,
            extensionSet: _emailMarkdown,
          ),
        ],
      ),
    );
  }
}

class _Banner extends StatelessWidget {
  const _Banner({required this.text, required this.isError});

  final String text;
  final bool isError;

  @override
  Widget build(BuildContext context) {
    final scheme = Theme.of(context).colorScheme;
    return Container(
      padding: const EdgeInsets.all(12),
      decoration: BoxDecoration(
        color: isError ? scheme.errorContainer : scheme.secondaryContainer,
        borderRadius: BorderRadius.circular(8),
      ),
      child: Text(
        text,
        style: TextStyle(
            color: isError
                ? scheme.onErrorContainer
                : scheme.onSecondaryContainer),
      ),
    );
  }
}
