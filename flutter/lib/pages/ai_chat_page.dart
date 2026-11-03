import 'dart:async';
import 'dart:convert';
import 'dart:ui';
import 'package:flutter/material.dart';
import 'package:flutter_markdown/flutter_markdown.dart';
import 'package:go_router/go_router.dart';
import 'package:google_fonts/google_fonts.dart';
import 'package:http/http.dart' as http;
import '../config/app_config.dart';
import '../l10n/generated/app_localizations.dart';
import '../services/sse_parser.dart';
import '../services/analytics_service.dart';
import '../services/api_client.dart';
import '../services/ai_service.dart';
import '../services/billing_service.dart';
import '../utils/chat_errors.dart';
import '../utils/typewriter.dart';
import '../widgets/ai_sparkle.dart';
import '../widgets/app_toast.dart';
import '../widgets/ai/ai_rich_message.dart';
import '../widgets/ai/conversation_history_sheet.dart';
import '../widgets/ai/tool_action_cards.dart';
import '../widgets/ai_consent_sheet.dart';

// ── VaporClinic Palette ─────────────────────────────────
const _accent = Color(0xFF7B61FF);
const _textGhost = Color(0xFFF0EFF4);
const _textMuted = Color(0xFF8A8A9A);
const _cardBg = Color(0x0DFFFFFF);
const _cardBorder = Color(0x14FFFFFF);
const _sheetBg = Color(0xFF0C0C18);
const _userBubble = Color(0xCC7B61FF); // ~80% \u2014 translucent so background bubbles show through
const _aiBubble = Color(0x1AFFFFFF);
const _expense = Color(0xFFF87171);

// ── Memory Type Icons ───────────────────────────────────
const _memoryTypeIcons = <String, IconData>{
  'date': Icons.calendar_today,
  'holiday': Icons.star_outline,
  'preference': Icons.favorite_outline,
  'goal': Icons.track_changes,
  'family': Icons.people_outline,
  'financial_habit': Icons.trending_up,
  'life_event': Icons.schedule,
};
const _memoryTypeLabels = <String, String>{
  'date': 'Important Date',
  'holiday': 'Holiday',
  'preference': 'Preference',
  'goal': 'Goal',
  'family': 'Family',
  'financial_habit': 'Financial Habit',
  'life_event': 'Life Event',
};

// ── Chat Message Model ──────────────────────────────────
class _ChatMessage {
  final String role; // 'user' | 'assistant'
  String content;
  final DateTime timestamp;
  bool isStreaming;
  List<ToolCallResult>? toolResults;
  ToolCallResult? pendingConfirmation;
  bool isFailed;
  String? originalMessage;
  List<ChatFileReference>? retryFiles;

  _ChatMessage({
    required this.role,
    required this.content,
    required this.timestamp,
    this.isStreaming = false,
    // ignore: unused_element_parameter
    this.toolResults,
    // ignore: unused_element_parameter
    this.pendingConfirmation,
    // ignore: unused_element_parameter
    this.isFailed = false,
    this.originalMessage,
    this.retryFiles,
  });
}

/// ─────────────────────────────────────────────────────────
///  AIChatPage
///
///  Full-featured AI financial assistant with:
///  - SSE streaming with tool execution events
///  - Conversation history sidebar
///  - Tool confirmation cards for write operations
///  - Action summaries for read/write results
///  - AI Memories modal with CRUD
///  - File upload (financial docs)
///  - Retry failed messages
///  - Suggestion chips
///  - Session management (new, load, delete)
///  - Cinematic VaporClinic glassmorphic design
/// ─────────────────────────────────────────────────────────
class AIChatPage extends StatefulWidget {
  const AIChatPage({super.key});

  @override
  State<AIChatPage> createState() => _AIChatPageState();
}

class _AIChatPageState extends State<AIChatPage>
    with SingleTickerProviderStateMixin {
  final _messageCtrl = TextEditingController();
  final _scrollCtrl = ScrollController();
  final _focusNode = FocusNode();

  late AnimationController _fadeCtrl;
  late Animation<double> _fadeAnim;

  final List<_ChatMessage> _messages = [];
  bool _isLoading = false;
  bool _isConfirming = false;
  bool _isRetrying = false;
  bool _userHasScrolled = false;
  String? _sessionId;

  // Word-by-word reveal of the answer (the backend sends it in one frame). The
  // list never follows the growth: on send it moves so the new exchange starts
  // near the top, then holds still while the words fill in below.
  Timer? _revealTimer;
  String _revealPending = '';
  int _revealIdx = -1;


  // File attachments
  final List<ChatFileReference> _attachedFiles = [];

  final _suggestions = [
    'How am I doing with my budget this month?',
    'What are my biggest spending categories?',
    'How can I save more money?',
    'Analyze my spending patterns',
    'Show me my savings progress',
    'What subscriptions am I paying for?',
    'Should I adjust my budget?',
  ];

  @override
  void initState() {
    super.initState();
    _fadeCtrl = AnimationController(
      vsync: this,
      duration: const Duration(milliseconds: 600),
    );
    _fadeAnim = CurvedAnimation(parent: _fadeCtrl, curve: Curves.easeOut);
    _fadeCtrl.forward();
    _addWelcomeMessage();

    _checkChatAccess();

    // Rebuild when the input focus changes so the border glow reacts.
    _focusNode.addListener(_onFocusChanged);

    _scrollCtrl.addListener(() {
      if (!_scrollCtrl.hasClients) return;
      final atBottom = _scrollCtrl.position.maxScrollExtent -
              _scrollCtrl.offset <
          100;
      if (_userHasScrolled != !atBottom) {
        _userHasScrolled = !atBottom;
      }
    });
  }

  /// AI Chat is Premium (server feature `ai_chat_bot`). Known up front so a Standard user
  /// who taps send sees the upgrade prompt, not the AI consent sheet followed by a
  /// refusal. Null until answered; the server still enforces the plan either way.
  bool? _chatAccess;

  Future<void> _checkChatAccess() async {
    try {
      final ok = await billingService.canAccessFeature('ai_chat_bot');
      if (mounted) _chatAccess = ok;
    } catch (_) {}
  }

  void _onFocusChanged() {
    if (mounted) setState(() {});
  }

  @override
  void dispose() {
    _revealTimer?.cancel();
    _fadeCtrl.dispose();
    _messageCtrl.dispose();
    _scrollCtrl.dispose();
    _focusNode.removeListener(_onFocusChanged);
    _focusNode.dispose();
    super.dispose();
  }

  void _addWelcomeMessage() {
    _messages.add(_ChatMessage(
      role: 'assistant',
      content:
          "Hello! I'm your AI financial assistant. I can help you understand your spending patterns, "
          "budget performance, savings goals, and provide personalized financial advice.\n\n"
          "What would you like to know about your finances?",
      timestamp: DateTime.now(),
    ));
  }

  /// After a send: the list has [_tailRoom] of empty space below the last
  /// bubble, so scrolling to the end puts the new question near the top of the
  /// view and leaves the answer room to fill in below without any further
  /// scrolling.
  void _scrollToBottom() {
    if (!_scrollCtrl.hasClients) return;
    Future.delayed(const Duration(milliseconds: 100), () {
      if (_scrollCtrl.hasClients) {
        _scrollCtrl.animateTo(
          _scrollCtrl.position.maxScrollExtent,
          duration: const Duration(milliseconds: 300),
          curve: Curves.easeOut,
        );
      }
    });
  }

  // ── Word-by-word reveal + soft follow ─────────────────

  /// Appends [text] to the reveal buffer for message [aiIdx]; a 50 ms timer
  /// moves a few words at a time into the bubble so the answer reads as it is
  /// being written rather than landing whole.
  void _queueReveal(int aiIdx, String text) {
    if (_revealIdx != aiIdx) {
      _revealIdx = aiIdx;
      _revealPending = '';
    }
    _revealPending += text;
    _revealTimer ??=
        Timer.periodic(const Duration(milliseconds: 50), (_) => _revealTick());
  }

  /// Drops anything not yet revealed for [aiIdx] and restarts from [text]
  /// (a confirmation frame replaces the running answer).
  void _replaceReveal(int aiIdx, String text) {
    _revealPending = '';
    if (mounted && aiIdx < _messages.length) {
      setState(() => _messages[aiIdx].content = '');
    }
    _queueReveal(aiIdx, text);
  }

  void _revealTick() {
    if (!mounted) {
      _revealTimer?.cancel();
      _revealTimer = null;
      return;
    }
    if (_revealPending.isEmpty) {
      _revealTimer?.cancel();
      _revealTimer = null;
      return;
    }
    final chunk = Typewriter.take(
        _revealPending, Typewriter.wordsPerTick(_revealPending.length));
    _revealPending = _revealPending.substring(chunk.length);
    setState(() {
      if (_revealIdx < _messages.length && _messages[_revealIdx].isStreaming) {
        _messages[_revealIdx].content += chunk;
      }
    });
  }

  /// Completes once every queued word is on screen, so the message is only
  /// finalised (spinner off, exact text set) after the reveal has caught up.
  Future<void> _drainReveal() async {
    while (mounted && _revealPending.isNotEmpty) {
      await Future<void>.delayed(const Duration(milliseconds: 50));
    }
  }

  // ── Send Message ──────────────────────────────────────

  Future<void> _sendMessage([String? suggestionText]) async {
    final text = suggestionText ?? _messageCtrl.text.trim();
    if (text.isEmpty && _attachedFiles.isEmpty) return;
    if (_isLoading) return;
    // Plan first: asking a Standard user to share data with Gemini for a feature their
    // plan doesn't include, then refusing them, is the worst order.
    if (_chatAccess == false) {
      _showSendError(const ChatUpgradeRequired());
      return;
    }
    // 5.1.2(i): explicit permission before the first message reaches Gemini. Most people
    // have already answered the app-level ask on the dashboard (maybeShowAiConsentPrompt).
    if (!await ensureAiConsent(context)) return;
    if (!mounted) return;

    analytics.track(AnalyticsEvents.aiChatMessageSent, {
      'has_attachment': _attachedFiles.isNotEmpty,
    });

    _messageCtrl.clear();
    _focusNode.unfocus();

    // Build display content
    final fileNames = _attachedFiles.map((f) => f.displayName).join(', ');
    final displayText = text.isNotEmpty
        ? (fileNames.isNotEmpty ? '$text\n\n📎 Attached: $fileNames' : text)
        : '[Analyzing: $fileNames]';
    final queryText = text.isNotEmpty
        ? text
        : 'Please analyze the attached file(s): $fileNames';

    setState(() {
      _messages.add(_ChatMessage(
        role: 'user',
        content: displayText,
        timestamp: DateTime.now(),
        originalMessage: queryText,
        retryFiles: _attachedFiles.isNotEmpty ? List.of(_attachedFiles) : null,
      ));
      _isLoading = true;
      _userHasScrolled = false;
    });

    final aiIdx = _messages.length;
    setState(() {
      _messages.add(_ChatMessage(
        role: 'assistant',
        content: '',
        timestamp: DateTime.now(),
        isStreaming: true,
      ));
    });
    _scrollToBottom();

    final filesToSend = List<ChatFileReference>.from(_attachedFiles);
    setState(() => _attachedFiles.clear());

    try {
      await _streamChat(queryText, aiIdx, filesToSend);
    } catch (e) {
      // Remove AI placeholder, mark user msg failed
      setState(() {
        if (_messages.length > aiIdx) _messages.removeAt(aiIdx);
        final userIdx = aiIdx - 1;
        if (userIdx >= 0 && _messages[userIdx].role == 'user') {
          _messages[userIdx].isFailed = true;
          _messages[userIdx].originalMessage = queryText;
          _messages[userIdx].retryFiles =
              filesToSend.isNotEmpty ? filesToSend : null;
        }
      });
      _showSendError(e);
    } finally {
      setState(() {
        _isLoading = false;
        _isRetrying = false;
      });
    }
  }

  // ── Retry Failed Message ──────────────────────────────

  Future<void> _retryMessage(int userIdx) async {
    final msg = _messages[userIdx];
    if (!msg.isFailed || msg.originalMessage == null) return;

    setState(() {
      _isRetrying = true;
      msg.isFailed = false;
    });

    final aiIdx = _messages.length;
    setState(() {
      _messages.add(_ChatMessage(
        role: 'assistant',
        content: '',
        timestamp: DateTime.now(),
        isStreaming: true,
      ));
      _isLoading = true;
      _userHasScrolled = false;
    });
    _scrollToBottom();

    try {
      await _streamChat(
        msg.originalMessage!,
        aiIdx,
        msg.retryFiles ?? [],
      );
    } catch (e) {
      setState(() {
        if (_messages.length > aiIdx) _messages.removeAt(aiIdx);
        msg.isFailed = true;
      });
      _showSendError(e, isRetry: true);
    } finally {
      setState(() {
        _isLoading = false;
        _isRetrying = false;
      });
    }
  }

  void _showSendError(Object error, {bool isRetry = false}) {
    if (!mounted) return;
    final described = describeChatSendError(error, isRetry: isRetry);
    final upgrade = described.offerUpgrade;
    // The toast outlives this page (it lives on the shell's ScaffoldMessenger, for 8 seconds). Tapping
    // "See plans" after leaving the chat used this State's context, which was gone: Sentry FLUTTER-1E, a
    // null check on the Accounts page. The router does not depend on this page being alive.
    final router = GoRouter.of(context);
    showAppToast(
      context,
      described.message,
      type: upgrade ? AppToastType.neutral : AppToastType.error,
      actionLabel: upgrade ? 'See plans' : null,
      onAction: upgrade ? () => router.push('/billing') : null,
      duration: upgrade ? const Duration(seconds: 8) : const Duration(seconds: 4),
    );
  }

  // ── Core SSE Streaming ────────────────────────────────

  Future<void> _streamChat(
    String userMessage,
    int aiIdx,
    List<ChatFileReference> files,
  ) async {
    final started = DateTime.now();
    var firstByteMs = 0;
    var toolCallCount = 0;

    var token = await apiClient.getToken();
    if (token == null) throw Exception('Not authenticated');

    // Build conversation history (exclude streaming placeholder)
    final history = _messages
        .where((m) => !m.isStreaming && m.content.isNotEmpty)
        .map((m) => {'role': m.role, 'content': m.content})
        .toList();

    final body = jsonEncode({
      'message': userMessage,
      if (_sessionId != null) 'sessionId': _sessionId,
      if (history.isNotEmpty) 'conversationHistory': history,
      if (files.isNotEmpty)
        'files': files
            .map((f) => {
                  'fileUri': f.fileUri,
                  'mimeType': f.mimeType,
                  'displayName': f.displayName,
                  'sizeBytes': f.sizeBytes,
                })
            .toList(),
    });

    // This SSE stream is hand-rolled on package:http (Dio doesn't surface a
    // chunked byte stream cleanly), so it must replicate the resilient Dio
    // client's auth handling: on a 401, silently refresh the session once and
    // retry before surfacing the failure; on a hard 401, drop the session via
    // onUnauthorized just like the interceptor does. The client is always
    // closed in the finally so we don't leak a socket per message.
    final client = http.Client();
    try {
      var streamedResponse = await _sendChatRequest(client, token, body);

      if (streamedResponse.statusCode == 401) {
        final refreshed = await apiClient.refreshSession();
        final newToken = refreshed ? await apiClient.getToken() : null;
        if (newToken != null) {
          token = newToken;
          streamedResponse = await _sendChatRequest(client, token, body);
        }
        if (streamedResponse.statusCode == 401) {
          apiClient.onUnauthorized?.call();
          throw Exception('401: Unauthorized');
        }
      }

      if (streamedResponse.statusCode == 429) {
        throw Exception('429: Rate limit exceeded');
      }
      if (streamedResponse.statusCode == 403) {
        final errBody = await streamedResponse.stream.bytesToString();
        // The server tags a plan refusal with PremiumRequired (a missing AI consent is a
        // different 403, AiConsentRequired, handled before sending).
        if (errBody.contains('PremiumRequired')) throw const ChatUpgradeRequired();
        throw Exception('HTTP 403: $errBody');
      }
      if (streamedResponse.statusCode != 200) {
        final errBody = await streamedResponse.stream.bytesToString();
        throw Exception('HTTP ${streamedResponse.statusCode}: $errBody');
      }

      String accumulated = '';
      final toolResults = <ToolCallResult>[];
      ToolCallResult? pendingConfirmation;
      var malformedFrames = 0;

      // parseSseStream buffers across transport chunks. This previously did
      // `chunk.split('\n')` per chunk, which is only correct if every frame happens to
      // land inside one chunk. It does not: a frame cut in half produced truncated JSON
      // (silently swallowed by a bare catch) followed by a prefix-less fragment (ignored),
      // losing the whole frame. That is how a 4,283-character answer rendered blank.
      await for (final event in parseSseStream(streamedResponse.stream)) {
        if (firstByteMs == 0) {
          firstByteMs = DateTime.now().difference(started).inMilliseconds;
        }
        {
          {
            final json = decodeSseData(
              event.data,
              onMalformed: (raw, error) {
                malformedFrames++;
                debugPrint('SSE frame failed to decode: $error');
              },
            );
            if (json != null) {
              final eventType = event.type ?? json['type'] as String? ?? '';

              switch (eventType) {
                case 'text':
                  final text = json['text'] as String? ??
                      json['content'] as String? ??
                      '';
                  accumulated += text;
                  // Revealed word by word; the follow-scroll happens per tick,
                  // never a jump to the bottom of a whole answer.
                  if (mounted) _queueReveal(aiIdx, text);
                  break;

              case 'function_result':
                toolCallCount++;
                final fnName = json['functionName'] as String?;
                final fnResult = json['functionResult'] as String?;
                // A write's preview arrives here too, before confirmation_required.
                // It hasn't run yet, so it is not an action taken.
                if (fnName != null &&
                    fnResult != null &&
                    !ToolPreview.isPreview(fnResult)) {
                  toolResults.add(ToolCallResult(
                    toolName: fnName,
                    arguments: json['arguments'] as Map<String, dynamic>? ?? {},
                    result: fnResult,
                    requiresConfirmation: false,
                  ));
                }
                break;

              case 'confirmation_required':
                if (json['toolResult'] != null) {
                  pendingConfirmation = ToolCallResult.fromJson(
                    json['toolResult'] as Map<String, dynamic>,
                  );
                  if (json['text'] != null) {
                    accumulated = json['text'] as String;
                    if (mounted) _replaceReveal(aiIdx, accumulated);
                  }
                }
                break;

              case 'error':
                final err = json['error'] as String? ??
                    'Something went wrong. Please try again.';
                final errText =
                    accumulated.isNotEmpty ? '\n\n⚠️ $err' : '⚠️ $err';
                accumulated += errText;
                if (mounted) _queueReveal(aiIdx, errText);
                break;

              case 'done':
                // Only capture the session id here. The message is finalized
                // once after the stream ends (below) so an empty response can be
                // detected and routed to the retry path — finalizing here would
                // set isStreaming=false and bypass that check, leaving a blank
                // bubble with no way to retry.
                if (json['sessionId'] != null) {
                  _sessionId = json['sessionId'] as String;
                }
                break;
            }
            }
          }
        }
      }

      // Finalize once, after the stream ends.
      //
      // Tool results deliberately do NOT count as content. They used to, and that is why
      // the production failure presented as a dead bubble rather than a retryable error:
      // get_accounts had run, so toolResults was non-empty, so this guard passed even
      // though the assistant had said nothing. The user saw an "ACTIONS TAKEN" chip above
      // an empty message. A chip is not an answer, so only text or a pending confirmation
      // counts as having responded.
      final hasContent =
          accumulated.trim().isNotEmpty || pendingConfirmation != null;
      if (!hasContent) {
        throw Exception(malformedFrames > 0
            ? 'The response was interrupted in transit. Please try again.'
            : 'The assistant returned an empty response.');
      }
      // The backend yields the answer as ONE text event, so "time to first byte"
      // is effectively the whole server think-time (model + any tool round
      // trips) and total_ms only exceeds it by the tail of the transfer. If a
      // future slowness report shows a small total_ms, the server is not the
      // problem and the render path is.
      analytics.track(AnalyticsEvents.aiChatResponseReceived, {
        'first_byte_ms': firstByteMs,
        'total_ms': DateTime.now().difference(started).inMilliseconds,
        'response_chars': accumulated.length,
        'tool_calls': toolCallCount,
        'message_count': _messages.length,
      });

      // Let the reveal catch up before the spinner goes and the exact text lands.
      await _drainReveal();
      if (mounted) {
        setState(() {
          if (aiIdx < _messages.length && _messages[aiIdx].isStreaming) {
            _messages[aiIdx]
              ..isStreaming = false
              ..content = accumulated
              ..toolResults = toolResults.isNotEmpty ? toolResults : null
              ..pendingConfirmation = pendingConfirmation;
          }
        });
      }
    } finally {
      client.close();
    }
  }

  /// Builds and sends a single SSE chat request on [client]. A fresh
  /// [http.Request] per call (http requests are single-use), so the caller can
  /// retry after a token refresh.
  Future<http.StreamedResponse> _sendChatRequest(
    http.Client client,
    String token,
    String body,
  ) {
    final request = http.Request(
      'POST',
      Uri.parse('${apiClient.baseUrl}/ai/chat/stream'),
    );
    request.headers['Authorization'] = 'Bearer $token';
    request.headers['Content-Type'] = 'application/json';
    // Tells the backend where the answer is read, so a phone gets no markdown tables.
    request.headers['X-Client-Type'] = AppConfig.clientType;
    request.body = body;
    return client.send(request);
  }

  // ── Tool Confirm / Cancel ─────────────────────────────

  Future<void> _confirmTool(int msgIdx) async {
    final msg = _messages[msgIdx];
    final pending = msg.pendingConfirmation;
    if (pending == null) return;

    setState(() => _isConfirming = true);
    try {
      final response = await aiService.executeToolConfirmed(
        toolName: pending.toolName,
        arguments: pending.arguments,
        confirmationToken: pending.confirmationToken,
      );
      // The token is spent either way, so the card goes; the bubble says what happened.
      final result = response['result'] as String?;
      final outcome = ToolOutcome.fromResult(result);
      setState(() {
        if (outcome.success) {
          final completed = ToolCallResult(
            toolName: pending.toolName,
            arguments: pending.arguments,
            result: result ?? 'Done',
            requiresConfirmation: false,
          );
          msg.toolResults = [...(msg.toolResults ?? []), completed];
        }
        msg.pendingConfirmation = null;
        msg.content = settleConfirmationText(
          msg.content,
          outcome.success ? '✅ ${outcome.message}' : '⚠️ ${outcome.message}',
        );
      });
      if (mounted) {
        if (outcome.success) {
          showAppToast(context, 'Action completed successfully.',
              type: AppToastType.success, duration: const Duration(seconds: 2));
        } else {
          showAppToast(context, 'Failed to complete action. Please try again.',
              type: AppToastType.error);
        }
      }
    } catch (e) {
      if (mounted) {
        showAppToast(context, 'Failed to complete action. Please try again.',
            type: AppToastType.error);
      }
    } finally {
      setState(() => _isConfirming = false);
    }
  }

  void _cancelTool(int msgIdx) {
    setState(() {
      final msg = _messages[msgIdx];
      msg.pendingConfirmation = null;
      msg.content = settleConfirmationText(msg.content, '❌ Action cancelled.');
    });
    if (mounted) {
      showAppToast(context, 'Action cancelled.',
          duration: const Duration(seconds: 2));
    }
  }

  // ── Conversation Management ───────────────────────────

  Future<void> _loadConversation(String sessionId) async {
    try {
      final detail = await aiService.getConversation(sessionId);
      if (!mounted) return;
      setState(() {
        _messages.clear();
        for (final msg in detail.messages) {
          _messages.add(_ChatMessage(
            role: msg.role,
            content: msg.content,
            timestamp: msg.createdAt,
          ));
        }
        _sessionId = sessionId;
      });
    } catch (e) {
      if (mounted) {
        showAppToast(context, 'Failed to load conversation',
            type: AppToastType.error);
      }
    }
  }

  void _startNewConversation() {
    setState(() {
      _sessionId = null;
      _messages.clear();
      _addWelcomeMessage();
    });
  }

  void _clearChat() {
    showModalBottomSheet(
      context: context,
      backgroundColor: Colors.transparent,
      builder: (_) => _ConfirmSheet(
        title: 'Clear Conversation',
        message:
            'This will clear the current conversation. Are you sure?',
        confirmLabel: 'Clear',
        onConfirm: () {
          Navigator.pop(context);
          _startNewConversation();
          showAppToast(context, 'Conversation cleared',
              type: AppToastType.success, duration: const Duration(seconds: 2));
        },
      ),
    );
  }

  // ── Build ─────────────────────────────────────────────

  @override
  Widget build(BuildContext context) {
    return FadeTransition(
      opacity: _fadeAnim,
      child: Column(
        children: [
          // ── Header ──
          _buildHeader(),
          // ── Messages ──
          Expanded(child: _buildMessageList()),
          // ── Suggestions ──
          _buildSuggestionChips(),
          // ── File attachments ──
          if (_attachedFiles.isNotEmpty) _buildAttachedFiles(),
          // ── Input area ──
          _buildInputArea(),
        ],
      ),
    );
  }

  // ── Header Bar ────────────────────────────────────────

  Widget _buildHeader() {
    return ClipRRect(
      child: BackdropFilter(
        filter: ImageFilter.blur(sigmaX: 20, sigmaY: 20),
        child: Container(
          padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 12),
          decoration: const BoxDecoration(
            color: Color(0x0DFFFFFF),
            border: Border(bottom: BorderSide(color: _cardBorder)),
          ),
          child: SafeArea(
            bottom: false,
            child: Row(
              children: [
                // History button
                _GlassIconButton(
                  icon: Icons.menu,
                  tooltip: 'Chat history',
                  onTap: _showHistorySidebar,
                ),
                const SizedBox(width: 12),
                // Icon + Title
                Container(
                  padding: const EdgeInsets.all(6),
                  decoration: BoxDecoration(
                    gradient: const LinearGradient(
                      colors: [_accent, Color(0xFF9F7AEA)],
                    ),
                    borderRadius: BorderRadius.circular(8),
                  ),
                  child: const Icon(Icons.auto_awesome, size: 16, color: _textGhost),
                ),
                const SizedBox(width: 10),
                // Title + PRO badge — flex so they can shrink on narrow
                // phones instead of pushing the action buttons off-screen.
                Expanded(
                  child: Row(
                    mainAxisSize: MainAxisSize.min,
                    children: [
                      Flexible(
                        child: Text(
                          AppLocalizations.of(context).aiChatTitle,
                          style: GoogleFonts.sora(
                            color: _textGhost,
                            fontSize: 16,
                            fontWeight: FontWeight.w600,
                          ),
                          overflow: TextOverflow.ellipsis,
                          maxLines: 1,
                        ),
                      ),
                      const SizedBox(width: 8),
                      Container(
                        padding:
                            const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
                        decoration: BoxDecoration(
                          color: _accent.withValues(alpha: 0.2),
                          borderRadius: BorderRadius.circular(12),
                          border: Border.all(color: _accent.withValues(alpha: 0.3)),
                        ),
                        child: Text(
                          AppLocalizations.of(context).aiChatProBadge,
                          style: GoogleFonts.sora(
                            color: _accent,
                            fontSize: 10,
                            fontWeight: FontWeight.w700,
                          ),
                        ),
                      ),
                    ],
                  ),
                ),
                // Session status
                if (_sessionId != null) ...[
                  Text(
                    AppLocalizations.of(context).aiChatSaved,
                    style: GoogleFonts.sora(color: _textMuted, fontSize: 11),
                  ),
                  const SizedBox(width: 8),
                ],
                // Memories button
                _GlassIconButton(
                  icon: Icons.psychology_outlined,
                  tooltip: 'AI Memories',
                  onTap: _showMemoriesSheet,
                ),
                const SizedBox(width: 4),
                // Clear button
                _GlassIconButton(
                  icon: Icons.refresh,
                  tooltip: 'Clear conversation',
                  onTap: _clearChat,
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  // ── Message List ──────────────────────────────────────

  /// Empty space kept below the last bubble once the user has sent something:
  /// it lets the newest exchange scroll up to the top of the view so an answer
  /// fills downward into free space instead of arriving below the fold.
  double _tailRoom(double viewportHeight) =>
      _messages.any((m) => m.role == 'user') ? viewportHeight * 0.55 : 0;

  Widget _buildMessageList() {
    return LayoutBuilder(
      builder: (context, constraints) => ListView.builder(
        controller: _scrollCtrl,
        padding: EdgeInsets.fromLTRB(
            16, 16, 16, 8 + _tailRoom(constraints.maxHeight)),
        itemCount: _messages.length + (_isLoading && _messages.isNotEmpty && !_messages.last.isStreaming ? 1 : 0),
        itemBuilder: (context, index) {
          if (index >= _messages.length) {
            return _buildThinkingIndicator();
          }
          return _buildMessageBubble(index);
        },
      ),
    );
  }

  Widget _buildThinkingIndicator() {
    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: Row(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          _aiAvatar(),
          const SizedBox(width: 10),
          // No BackdropFilter here. A blur re-samples its backdrop every frame
          // the backdrop changes, and the backdrop is InteractiveBackgroundLayer,
          // which repaints at 60fps forever. Pairing that with the spinner inside
          // meant a full-size gaussian blur every frame for the entire (often
          // multi-second, tool-calling) wait. Solid fill matches GlassCard.
          RepaintBoundary(
            child: Container(
              padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
              decoration: BoxDecoration(
                color: _aiBubble,
                borderRadius: BorderRadius.circular(16),
                border: Border.all(color: _cardBorder),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const SizedBox(
                    width: 16,
                    height: 16,
                    child: CircularProgressIndicator(
                      strokeWidth: 2,
                      color: _accent,
                    ),
                  ),
                  const SizedBox(width: 10),
                  Text(
                    AppLocalizations.of(context).aiChatThinking,
                    style: GoogleFonts.sora(color: _textMuted, fontSize: 13),
                  ),
                ],
              ),
            ),
          ),
        ],
      ),
    );
  }

  // ── Single Message Bubble ─────────────────────────────

  Widget _buildMessageBubble(int index) {
    // RepaintBoundary per bubble: the page setState()s on every stream event and
    // on every focus change, and without one each of those repaints every
    // visible bubble's text rather than just the bubble that changed.
    return RepaintBoundary(child: _buildMessageBubbleContent(index));
  }

  Widget _buildMessageBubbleContent(int index) {
    final msg = _messages[index];
    final isUser = msg.role == 'user';

    return Padding(
      padding: const EdgeInsets.only(bottom: 16),
      child: Row(
        mainAxisAlignment:
            isUser ? MainAxisAlignment.end : MainAxisAlignment.start,
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (!isUser) ...[
            _aiAvatar(),
            const SizedBox(width: 10),
          ],
          Flexible(
            child: Column(
              crossAxisAlignment:
                  isUser ? CrossAxisAlignment.end : CrossAxisAlignment.start,
              children: [
                // ── Action summary (tool results) ──
                if (!isUser &&
                    msg.toolResults != null &&
                    msg.toolResults!.isNotEmpty)
                  ToolActionSummary(toolResults: msg.toolResults!),

                // ── Message content ──
                //
                // Deliberately NOT a BackdropFilter. This surface is per message,
                // so the cost grew with the length of the conversation, and every
                // blur re-runs whenever its backdrop changes — which, over
                // InteractiveBackgroundLayer, is every frame, forever. A long
                // answer therefore left the page recomputing several full-width
                // gaussian blurs at 60fps for as long as it stayed open: slow on
                // a mid-range phone, and instantly fine again on navigating away.
                // The fill + hairline border + purple glow below is the canonical
                // GlassCard treatment every other surface in the app already
                // uses, so the look holds — it just no longer costs a blur.
                Container(
                  constraints: BoxConstraints(
                    // An answer with a table gets nearly the full width; the
                    // avatar column still takes its share through Flexible.
                    maxWidth: MediaQuery.of(context).size.width *
                        (!isUser && _hasTable(msg.content) ? 0.95 : 0.78),
                  ),
                  padding:
                      const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
                  decoration: BoxDecoration(
                    color: isUser ? _userBubble : _aiBubble,
                    borderRadius: BorderRadius.only(
                      topLeft: const Radius.circular(18),
                      topRight: const Radius.circular(18),
                      bottomLeft: Radius.circular(isUser ? 18 : 4),
                      bottomRight: Radius.circular(isUser ? 4 : 18),
                    ),
                    border: Border.all(
                      color:
                          isUser ? _accent.withValues(alpha: 0.3) : _cardBorder,
                    ),
                    boxShadow: [
                      BoxShadow(
                        color: const Color(0xFF7B61FF)
                            .withValues(alpha: isUser ? 0.18 : 0.05),
                        blurRadius: isUser ? 16 : 22,
                        spreadRadius: 0,
                      ),
                    ],
                  ),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      if (isUser)
                        Text(
                          msg.content,
                          style: GoogleFonts.sora(
                            color: Colors.white,
                            fontSize: 14,
                            height: 1.5,
                          ),
                        )
                      else if (msg.content.isEmpty && msg.isStreaming)
                        Text(
                          '...',
                          style:
                              GoogleFonts.sora(color: _textMuted, fontSize: 14),
                        )
                      else
                        // #388 Phase 2: renders markdown + any model-authored
                        // ```svg visualizations the assistant includes.
                        AiRichMessage(
                          content: msg.content,
                          styleSheet: _mdStyle,
                          selectable: !msg.isStreaming,
                          streaming: msg.isStreaming,
                        ),
                      if (msg.isStreaming)
                        Padding(
                          padding: const EdgeInsets.only(top: 8),
                          child: SizedBox(
                            width: 14,
                            height: 14,
                            child: CircularProgressIndicator(
                              strokeWidth: 2,
                              color: _accent,
                            ),
                          ),
                        ),
                      // Timestamp
                      Padding(
                        padding: const EdgeInsets.only(top: 6),
                        child: Text(
                          _formatTime(msg.timestamp),
                          style: GoogleFonts.sora(
                            color: isUser ? Colors.white70 : _textMuted,
                            fontSize: 10,
                          ),
                        ),
                      ),
                    ],
                  ),
                ),

                // ── Failed badge + retry ──
                if (isUser && msg.isFailed)
                  Padding(
                    padding: const EdgeInsets.only(top: 6),
                    child: Row(
                      mainAxisSize: MainAxisSize.min,
                      children: [
                        Container(
                          padding: const EdgeInsets.symmetric(
                              horizontal: 8, vertical: 3),
                          decoration: BoxDecoration(
                            color: _expense.withValues(alpha: 0.2),
                            borderRadius: BorderRadius.circular(8),
                          ),
                          child: Text(
                            AppLocalizations.of(context).aiChatFailedToSend,
                            style: GoogleFonts.sora(
                              color: _expense,
                              fontSize: 11,
                              fontWeight: FontWeight.w500,
                            ),
                          ),
                        ),
                        const SizedBox(width: 8),
                        GestureDetector(
                          onTap: _isRetrying ? null : () => _retryMessage(index),
                          child: Container(
                            padding: const EdgeInsets.symmetric(
                                horizontal: 10, vertical: 3),
                            decoration: BoxDecoration(
                              border: Border.all(color: _accent.withValues(alpha: 0.5)),
                              borderRadius: BorderRadius.circular(8),
                            ),
                            child: Row(
                              mainAxisSize: MainAxisSize.min,
                              children: [
                                Icon(Icons.replay, size: 12, color: _accent),
                                const SizedBox(width: 4),
                                Text(
                                  _isRetrying
                                      ? AppLocalizations.of(context)
                                          .aiChatRetrying
                                      : AppLocalizations.of(context).aiChatRetry,
                                  style: GoogleFonts.sora(
                                    color: _accent,
                                    fontSize: 11,
                                    fontWeight: FontWeight.w500,
                                  ),
                                ),
                              ],
                            ),
                          ),
                        ),
                      ],
                    ),
                  ),

                // ── Tool confirmation card ──
                if (!isUser && msg.pendingConfirmation != null)
                  ToolConfirmationCard(
                    toolResult: msg.pendingConfirmation!,
                    isLoading: _isConfirming,
                    onConfirm: () => _confirmTool(index),
                    onCancel: () => _cancelTool(index),
                  ),
              ],
            ),
          ),
          if (isUser) ...[
            const SizedBox(width: 10),
            _userAvatar(),
          ],
        ],
      ),
    );
  }

  Widget _aiAvatar() {
    return Container(
      padding: const EdgeInsets.all(8),
      decoration: BoxDecoration(
        gradient: const LinearGradient(colors: [_accent, Color(0xFF9F7AEA)]),
        shape: BoxShape.circle,
      ),
      child: const Icon(Icons.auto_awesome, size: 14, color: _textGhost),
    );
  }

  Widget _userAvatar() {
    return CircleAvatar(
      radius: 15,
      backgroundColor: _accent.withValues(alpha: 0.3),
      child: const Icon(Icons.person, size: 14, color: _textGhost),
    );
  }

  // ── Suggestion Chips ──────────────────────────────────

  Widget _buildSuggestionChips() {
    return Container(
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 20),
            child: Row(
              mainAxisSize: MainAxisSize.min,
              children: [
                ShaderMask(
                  shaderCallback: (bounds) =>
                      kAiGradient.createShader(bounds),
                  blendMode: BlendMode.srcIn,
                  child: const Icon(Icons.auto_awesome,
                      size: 12, color: Colors.white),
                ),
                const SizedBox(width: 6),
                Text(
                  _messages.length <= 1
                      ? AppLocalizations.of(context).aiChatTryAsking
                      : AppLocalizations.of(context).aiChatIdeas,
                  style: GoogleFonts.sora(
                      color: _textMuted,
                      fontSize: 11,
                      letterSpacing: 0.4,
                      fontWeight: FontWeight.w500),
                ),
              ],
            ),
          ),
          const SizedBox(height: 8),
          SizedBox(
            height: 32,
            child: ListView.separated(
              scrollDirection: Axis.horizontal,
              padding: const EdgeInsets.symmetric(horizontal: 20),
              itemCount: _suggestions.length,
              separatorBuilder: (_, __) => const SizedBox(width: 8),
              itemBuilder: (_, i) {
                return _SuggestionChip(
                  label: _suggestions[i],
                  onTap: () {
                    _messageCtrl.text = _suggestions[i];
                    _focusNode.requestFocus();
                  },
                );
              },
            ),
          ),
        ],
      ),
    );
  }

  // ── Attached Files Display ────────────────────────────

  Widget _buildAttachedFiles() {
    return Container(
      padding: const EdgeInsets.symmetric(horizontal: 20, vertical: 4),
      child: Wrap(
        spacing: 8,
        runSpacing: 4,
        children: [
          for (int i = 0; i < _attachedFiles.length; i++)
            Container(
              padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
              decoration: BoxDecoration(
                color: _accent.withValues(alpha: 0.12),
                borderRadius: BorderRadius.circular(12),
                border: Border.all(color: _accent.withValues(alpha: 0.2)),
              ),
              child: Row(
                mainAxisSize: MainAxisSize.min,
                children: [
                  const Icon(Icons.attach_file, size: 12, color: _accent),
                  const SizedBox(width: 4),
                  Text(
                    _attachedFiles[i].displayName,
                    style: GoogleFonts.sora(
                        color: _textGhost, fontSize: 11),
                  ),
                  const SizedBox(width: 6),
                  GestureDetector(
                    onTap: () => setState(
                        () => _attachedFiles.removeAt(i)),
                    child: const Icon(Icons.close, size: 12, color: _textMuted),
                  ),
                ],
              ),
            ),
        ],
      ),
    );
  }

  // ── Input Area ────────────────────────────────────────

  Widget _buildInputArea() {
    return ClipRRect(
      borderRadius: const BorderRadius.vertical(top: Radius.circular(16)),
      child: BackdropFilter(
        filter: ImageFilter.blur(sigmaX: 20, sigmaY: 20),
        child: Container(
          padding: const EdgeInsets.fromLTRB(16, 8, 16, 16),
          decoration: BoxDecoration(
            color: const Color(0x0DFFFFFF),
            borderRadius: const BorderRadius.vertical(top: Radius.circular(16)),
            border: const Border(top: BorderSide(color: _cardBorder)),
          ),
          child: SafeArea(
            top: false,
            child: Column(
              children: [
                Row(
                  crossAxisAlignment: CrossAxisAlignment.end,
                  children: [
                    // Attach file button
                    _GlassIconButton(
                      icon: Icons.attach_file,
                      tooltip: 'Attach file',
                      onTap: _isLoading ? null : _pickFile,
                    ),
                    const SizedBox(width: 8),
                    // Text field — wrapped in a single Container that owns
                    // the rounded outline. All InputBorder variants are
                    // explicitly set to none so the default Material outline
                    // doesn't render a second pill-shaped border on top of
                    // ours (see #163).
                    Expanded(
                      child: AnimatedContainer(
                        duration: const Duration(milliseconds: 180),
                        constraints: const BoxConstraints(maxHeight: 120),
                        decoration: BoxDecoration(
                          color: const Color(0x0DFFFFFF),
                          borderRadius: BorderRadius.circular(24),
                          border: Border.all(
                            color: _focusNode.hasFocus
                                ? _accent.withValues(alpha: 0.55)
                                : _cardBorder,
                            width: _focusNode.hasFocus ? 1.2 : 1,
                          ),
                          boxShadow: _focusNode.hasFocus
                              ? [
                                  BoxShadow(
                                    color: _accent.withValues(alpha: 0.18),
                                    blurRadius: 16,
                                    spreadRadius: -2,
                                  ),
                                ]
                              : null,
                        ),
                        child: TextField(
                          controller: _messageCtrl,
                          focusNode: _focusNode,
                          maxLines: 4,
                          minLines: 1,
                          cursorColor: _accent,
                          style: GoogleFonts.sora(
                              color: _textGhost, fontSize: 14),
                          decoration: InputDecoration(
                            hintText: 'Ask about your finances...',
                            hintStyle: GoogleFonts.sora(
                                color: _textMuted, fontSize: 14),
                            // Defeat default Material InputDecorationTheme so
                            // we don't get a stacked outline on top of our
                            // Container border.
                            filled: false,
                            isDense: true,
                            border: InputBorder.none,
                            enabledBorder: InputBorder.none,
                            focusedBorder: InputBorder.none,
                            errorBorder: InputBorder.none,
                            disabledBorder: InputBorder.none,
                            contentPadding: const EdgeInsets.symmetric(
                                horizontal: 20, vertical: 14),
                          ),
                          onSubmitted: (_) => _sendMessage(),
                        ),
                      ),
                    ),
                    const SizedBox(width: 8),
                    // Send button — uses the canonical AI gradient (#163)
                    GestureDetector(
                      onTap: _isLoading ? null : () => _sendMessage(),
                      child: Container(
                        width: 44,
                        height: 44,
                        decoration: BoxDecoration(
                          gradient: kAiGradient,
                          shape: BoxShape.circle,
                          boxShadow: [
                            BoxShadow(
                              color: const Color(0xFFA78BFA)
                                  .withValues(alpha: 0.35),
                              blurRadius: 14,
                              offset: const Offset(0, 4),
                            ),
                          ],
                        ),
                        child: Center(
                          child: _isLoading
                              ? const SizedBox(
                                  width: 18,
                                  height: 18,
                                  child: CircularProgressIndicator(
                                    strokeWidth: 2,
                                    color: _textGhost,
                                  ),
                                )
                              : const Icon(Icons.send,
                                  size: 18, color: Colors.white),
                        ),
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                Text(
                  AppLocalizations.of(context).aiChatDisclaimer,
                  style: GoogleFonts.sora(color: _textMuted, fontSize: 10),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }

  // ── File Picker (web-safe) ────────────────────────────

  Future<void> _pickFile() async {
    // (stubbed for now, platform-specific file pickers would go here)
    if (mounted) {
      showAppToast(context, 'File upload coming soon — drag & drop supported on web',
          duration: const Duration(seconds: 2));
    }
  }

  // ── History Sidebar ───────────────────────────────────

  /// The sheet loads its own list and closes itself with its own context; this
  /// page acts on the owner's choice only if it is still mounted (FLUTTER-1C:
  /// sheet callbacks that popped with this page's context could run after the
  /// page was gone, and in release a deactivated context's Navigator lookup
  /// throws a null-check error).
  Future<void> _showHistorySidebar() async {
    final choice = await showModalBottomSheet<ConversationHistoryChoice>(
      context: context,
      backgroundColor: Colors.transparent,
      isScrollControlled: true,
      builder: (_) => ConversationHistorySheet(
        currentSessionId: _sessionId,
        loadConversations: () =>
            aiService.getConversations(context: 'chat', limit: 20),
        deleteConversation: aiService.deleteConversation,
        onDeleted: (sid) {
          if (mounted && _sessionId == sid) _startNewConversation();
        },
      ),
    );
    if (!mounted || choice == null) return;
    final sid = choice.sessionId;
    if (sid == null) {
      _startNewConversation();
    } else {
      _loadConversation(sid);
    }
  }

  // ── Memories Sheet ────────────────────────────────────

  void _showMemoriesSheet() {
    showModalBottomSheet(
      context: context,
      backgroundColor: Colors.transparent,
      isScrollControlled: true,
      builder: (_) => const _MemoriesSheet(),
    );
  }

  // ── Helpers ───────────────────────────────────────────

  /// Built once. This was previously rebuilt inside every message bubble on
  /// every frame — ~11 GoogleFonts lookups and a fresh stylesheet instance per
  /// bubble per rebuild, which is exactly the churn that starts to show once a
  /// conversation gets long.
  late final MarkdownStyleSheet _mdStyle = _markdownStyle();

  MarkdownStyleSheet _markdownStyle() {
    return MarkdownStyleSheet(
      p: GoogleFonts.sora(color: _textGhost, fontSize: 14, height: 1.6),
      h1: GoogleFonts.instrumentSerif(
        color: _textGhost, fontSize: 22, fontWeight: FontWeight.bold,
      ),
      h2: GoogleFonts.sora(
        color: _textGhost, fontSize: 18, fontWeight: FontWeight.w600,
      ),
      h3: GoogleFonts.sora(
        color: _textGhost, fontSize: 15, fontWeight: FontWeight.w600,
      ),
      strong: const TextStyle(fontWeight: FontWeight.bold, color: _accent),
      em: const TextStyle(fontStyle: FontStyle.italic, color: _textGhost),
      listBullet: GoogleFonts.sora(color: _textGhost, fontSize: 14),
      code: GoogleFonts.firaCode(
        color: _accent,
        fontSize: 12,
        backgroundColor: Color(0x1A7B61FF),
      ),
      codeblockDecoration: BoxDecoration(
        color: const Color(0x1A7B61FF),
        borderRadius: BorderRadius.circular(8),
        border: Border.all(color: _accent.withValues(alpha: 0.2)),
      ),
      blockquoteDecoration: BoxDecoration(
        border: Border(left: BorderSide(color: _accent, width: 3)),
      ),
      // Tables are condensed for the phone bubble: small type, tight cells.
      // The backend also asks the model not to send tables to phones at all;
      // this is for the ones that slip through.
      tableBorder: TableBorder.all(color: _cardBorder, width: 1),
      tableHead: GoogleFonts.sora(
        color: _textGhost, fontWeight: FontWeight.w600, fontSize: 11,
      ),
      tableBody: GoogleFonts.sora(color: _textGhost, fontSize: 11, height: 1.3),
      tableCellsPadding: const EdgeInsets.symmetric(horizontal: 5, vertical: 4),
      tableHeadAlign: TextAlign.left,
    );
  }

  /// A markdown table: a line starting with a pipe followed by a delimiter row.
  static bool _hasTable(String content) =>
      RegExp(r'^\s*\|.*\n\s*\|?\s*:?-{2,}', multiLine: true).hasMatch(content);

  String _formatTime(DateTime dt) {
    final h = dt.hour;
    final m = dt.minute.toString().padLeft(2, '0');
    final ampm = h >= 12 ? 'PM' : 'AM';
    final hour = h == 0 ? 12 : (h > 12 ? h - 12 : h);
    return '$hour:$m $ampm';
  }
}

// ═════════════════════════════════════════════════════════
//  Private Widget: AI Memories Sheet
// ═════════════════════════════════════════════════════════

class _MemoriesSheet extends StatefulWidget {
  const _MemoriesSheet();

  @override
  State<_MemoriesSheet> createState() => _MemoriesSheetState();
}

class _MemoriesSheetState extends State<_MemoriesSheet> {
  List<AiMemory> _memories = [];
  bool _memoryEnabled = false;
  bool _isLoading = true;
  int? _editingId;
  String _editValue = '';
  String _editDisplayText = '';
  bool _isSaving = false;

  @override
  void initState() {
    super.initState();
    _loadMemories();
  }

  Future<void> _loadMemories() async {
    try {
      final response = await aiService.getMemories();
      if (mounted) {
        setState(() {
          _memories = response.memories;
          _memoryEnabled = response.memoryEnabled;
          _isLoading = false;
        });
      }
    } catch (_) {
      if (mounted) setState(() => _isLoading = false);
    }
  }

  Future<void> _saveEdit(int id) async {
    setState(() => _isSaving = true);
    try {
      await aiService.updateMemory(id,
          value: _editValue, displayText: _editDisplayText);
      if (!mounted) return;
      setState(() {
        final idx = _memories.indexWhere((m) => m.id == id);
        if (idx >= 0) {
          // Reload memories to reflect update
          _loadMemories();
        }
        _editingId = null;
      });
    } catch (_) {
    } finally {
      if (mounted) setState(() => _isSaving = false);
    }
  }

  Future<void> _deactivate(int id) async {
    try {
      await aiService.deactivateMemory(id);
      if (!mounted) return;
      setState(() => _memories.removeWhere((m) => m.id == id));
    } catch (_) {}
  }

  Future<void> _deleteMemory(int id) async {
    try {
      await aiService.deleteMemory(id);
      if (!mounted) return;
      setState(() => _memories.removeWhere((m) => m.id == id));
    } catch (_) {}
  }

  Future<void> _deleteAll() async {
    try {
      await aiService.deleteAllMemories();
      if (!mounted) return;
      setState(() => _memories.clear());
    } catch (_) {}
  }

  @override
  Widget build(BuildContext context) {
    return Container(
      constraints: BoxConstraints(
        maxHeight: MediaQuery.of(context).size.height * 0.8,
      ),
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
        border: Border(top: BorderSide(color: _cardBorder)),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Handle
          Center(
            child: Container(
              margin: const EdgeInsets.only(top: 12),
              width: 40,
              height: 4,
              decoration: BoxDecoration(
                color: _textMuted.withValues(alpha: 0.3),
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          // Header
          Padding(
            padding: const EdgeInsets.fromLTRB(20, 16, 20, 12),
            child: Row(
              children: [
                const Icon(Icons.psychology, color: _accent, size: 22),
                const SizedBox(width: 10),
                Text(
                  AppLocalizations.of(context).aiChatMemoriesTitle,
                  style: GoogleFonts.instrumentSerif(
                    color: _textGhost,
                    fontSize: 22,
                    fontWeight: FontWeight.bold,
                  ),
                ),
                const Spacer(),
                if (_memories.isNotEmpty)
                  GestureDetector(
                    onTap: () => _confirmDeleteAll(context),
                    child: Text(
                      AppLocalizations.of(context).aiChatDeleteAll,
                      style: GoogleFonts.sora(
                        color: _expense,
                        fontSize: 12,
                        fontWeight: FontWeight.w500,
                      ),
                    ),
                  ),
              ],
            ),
          ),
          const Divider(color: _cardBorder, height: 1),
          // Content
          Flexible(
            child: _isLoading
                ? const Center(
                    child: Padding(
                      padding: EdgeInsets.all(32),
                      child: CircularProgressIndicator(color: _accent),
                    ),
                  )
                : !_memoryEnabled
                    ? _emptyState(
                        Icons.psychology_outlined,
                        'AI Memory is disabled. Enable it in Settings to allow the AI to remember facts about you.',
                      )
                    : _memories.isEmpty
                        ? _emptyState(
                            Icons.psychology_outlined,
                            'No memories yet! As you chat with the AI, it will remember important facts about you.',
                          )
                        : ListView.builder(
                            shrinkWrap: true,
                            padding: const EdgeInsets.all(16),
                            itemCount: _memories.length,
                            itemBuilder: (_, i) =>
                                _buildMemoryCard(_memories[i]),
                          ),
          ),
        ],
      ),
    );
  }

  Widget _emptyState(IconData icon, String text) {
    return Padding(
      padding: const EdgeInsets.all(32),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          Icon(icon, size: 48, color: _textMuted.withValues(alpha: 0.4)),
          const SizedBox(height: 16),
          Text(
            text,
            textAlign: TextAlign.center,
            style: GoogleFonts.sora(color: _textMuted, fontSize: 13),
          ),
        ],
      ),
    );
  }

  Widget _buildMemoryCard(AiMemory memory) {
    final isEditing = _editingId == memory.id;
    final typeIcon = _memoryTypeIcons[memory.memoryType] ?? Icons.psychology;
    final typeLabel = _memoryTypeLabels[memory.memoryType] ?? memory.memoryType;

    return Container(
      margin: const EdgeInsets.only(bottom: 10),
      padding: const EdgeInsets.all(14),
      decoration: BoxDecoration(
        color: _cardBg,
        borderRadius: BorderRadius.circular(12),
        border: Border.all(color: _cardBorder),
      ),
      child: isEditing
          ? Column(
              children: [
                TextField(
                  controller: TextEditingController(text: _editDisplayText),
                  onChanged: (v) => _editDisplayText = v,
                  style: GoogleFonts.sora(color: _textGhost, fontSize: 13),
                  decoration: InputDecoration(
                    hintText: 'Display text',
                    hintStyle: GoogleFonts.sora(color: _textMuted, fontSize: 13),
                    border: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(8),
                      borderSide: const BorderSide(color: _cardBorder),
                    ),
                    contentPadding: const EdgeInsets.symmetric(
                        horizontal: 12, vertical: 8),
                  ),
                ),
                const SizedBox(height: 8),
                TextField(
                  controller: TextEditingController(text: _editValue),
                  onChanged: (v) => _editValue = v,
                  maxLines: 2,
                  style: GoogleFonts.sora(color: _textGhost, fontSize: 13),
                  decoration: InputDecoration(
                    hintText: 'Value',
                    hintStyle: GoogleFonts.sora(color: _textMuted, fontSize: 13),
                    border: OutlineInputBorder(
                      borderRadius: BorderRadius.circular(8),
                      borderSide: const BorderSide(color: _cardBorder),
                    ),
                    contentPadding: const EdgeInsets.symmetric(
                        horizontal: 12, vertical: 8),
                  ),
                ),
                const SizedBox(height: 10),
                Row(
                  mainAxisAlignment: MainAxisAlignment.end,
                  children: [
                    GestureDetector(
                      onTap: () => setState(() => _editingId = null),
                      child: Text(AppLocalizations.of(context).aiChatCancel,
                          style: GoogleFonts.sora(
                              color: _textMuted, fontSize: 12)),
                    ),
                    const SizedBox(width: 16),
                    GestureDetector(
                      onTap: _isSaving ? null : () => _saveEdit(memory.id),
                      child: Container(
                        padding: const EdgeInsets.symmetric(
                            horizontal: 12, vertical: 5),
                        decoration: BoxDecoration(
                          color: _accent,
                          borderRadius: BorderRadius.circular(8),
                        ),
                        child: Text(
                          _isSaving
                              ? AppLocalizations.of(context).aiChatSaving
                              : AppLocalizations.of(context).aiChatSave,
                          style: GoogleFonts.sora(
                            color: _textGhost,
                            fontSize: 12,
                            fontWeight: FontWeight.w600,
                          ),
                        ),
                      ),
                    ),
                  ],
                ),
              ],
            )
          : Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(
                  children: [
                    Icon(typeIcon, size: 16, color: _accent),
                    const SizedBox(width: 6),
                    Container(
                      padding: const EdgeInsets.symmetric(
                          horizontal: 8, vertical: 2),
                      decoration: BoxDecoration(
                        color: _accent.withValues(alpha: 0.12),
                        borderRadius: BorderRadius.circular(6),
                      ),
                      child: Text(
                        typeLabel,
                        style: GoogleFonts.sora(
                          color: _accent,
                          fontSize: 10,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                    ),
                    if (memory.wasManuallyEdited) ...[
                      const SizedBox(width: 6),
                      Container(
                        padding: const EdgeInsets.symmetric(
                            horizontal: 6, vertical: 2),
                        decoration: BoxDecoration(
                          color: _textMuted.withValues(alpha: 0.15),
                          borderRadius: BorderRadius.circular(6),
                        ),
                        child: Text(
                          AppLocalizations.of(context).aiChatEdited,
                          style: GoogleFonts.sora(
                              color: _textMuted, fontSize: 9),
                        ),
                      ),
                    ],
                    const Spacer(),
                    // Edit
                    GestureDetector(
                      onTap: () {
                        setState(() {
                          _editingId = memory.id;
                          _editValue = memory.memoryValue;
                          _editDisplayText = memory.displayText;
                        });
                      },
                      child: const Padding(
                        padding: EdgeInsets.all(4),
                        child: Icon(Icons.edit_outlined,
                            size: 16, color: _textMuted),
                      ),
                    ),
                    // Hide
                    GestureDetector(
                      onTap: () => _deactivate(memory.id),
                      child: const Padding(
                        padding: EdgeInsets.all(4),
                        child: Icon(Icons.visibility_off_outlined,
                            size: 16, color: _textMuted),
                      ),
                    ),
                    // Delete
                    GestureDetector(
                      onTap: () => _confirmDelete(context, memory.id),
                      child: const Padding(
                        padding: EdgeInsets.all(4),
                        child: Icon(Icons.delete_outline,
                            size: 16, color: _expense),
                      ),
                    ),
                  ],
                ),
                const SizedBox(height: 8),
                Text(
                  memory.displayText,
                  style: GoogleFonts.sora(
                    color: _textGhost,
                    fontSize: 13,
                    fontWeight: FontWeight.w500,
                  ),
                ),
                const SizedBox(height: 8),
                Row(
                  children: [
                    Text(
                      AppLocalizations.of(context)
                          .aiChatLearnedDate(_fmtDate(memory.learnedAt)),
                      style: GoogleFonts.sora(color: _textMuted, fontSize: 10),
                    ),
                    if (memory.usageCount > 0) ...[
                      const SizedBox(width: 12),
                      Text(
                        AppLocalizations.of(context)
                            .aiChatUsedCount(memory.usageCount),
                        style:
                            GoogleFonts.sora(color: _textMuted, fontSize: 10),
                      ),
                    ],
                    const SizedBox(width: 12),
                    Text(
                      AppLocalizations.of(context).aiChatConfidencePercent(
                          (memory.confidence * 100).round()),
                      style: GoogleFonts.sora(color: _textMuted, fontSize: 10),
                    ),
                  ],
                ),
              ],
            ),
    );
  }

  void _confirmDelete(BuildContext ctx, int id) {
    showDialog(
      context: ctx,
      builder: (_) => AlertDialog(
        backgroundColor: _sheetBg,
        title: Text(AppLocalizations.of(ctx).aiChatDeleteMemoryTitle,
            style: GoogleFonts.sora(color: _textGhost)),
        content: Text(
          AppLocalizations.of(ctx).aiChatDeleteMemoryConfirm,
          style: GoogleFonts.sora(color: _textMuted),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(ctx),
            child: Text(AppLocalizations.of(ctx).aiChatCancel,
                style: GoogleFonts.sora(color: _textMuted)),
          ),
          TextButton(
            onPressed: () {
              Navigator.pop(ctx);
              _deleteMemory(id);
            },
            child: Text(AppLocalizations.of(ctx).aiChatDelete,
                style: GoogleFonts.sora(color: _expense)),
          ),
        ],
      ),
    );
  }

  void _confirmDeleteAll(BuildContext ctx) {
    showDialog(
      context: ctx,
      builder: (_) => AlertDialog(
        backgroundColor: _sheetBg,
        title: Text(AppLocalizations.of(ctx).aiChatDeleteAllMemoriesTitle,
            style: GoogleFonts.sora(color: _textGhost)),
        content: Text(
          AppLocalizations.of(ctx).aiChatDeleteAllConfirm,
          style: GoogleFonts.sora(color: _textMuted),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(ctx),
            child: Text(AppLocalizations.of(ctx).aiChatCancel,
                style: GoogleFonts.sora(color: _textMuted)),
          ),
          TextButton(
            onPressed: () {
              Navigator.pop(ctx);
              _deleteAll();
            },
            child: Text(AppLocalizations.of(ctx).aiChatDeleteAll,
                style: GoogleFonts.sora(color: _expense)),
          ),
        ],
      ),
    );
  }

  String _fmtDate(DateTime dt) {
    final months = [
      'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
      'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
    ];
    return '${months[dt.month - 1]} ${dt.day}, ${dt.year}';
  }
}

// ═════════════════════════════════════════════════════════
//  Private Widget: Suggestion Chip
//  Cinematic chip with subtle AI-gradient hairline + hover
//  treatment. Used in the AI chat suggestion strip (#163).
// ═════════════════════════════════════════════════════════

class _SuggestionChip extends StatefulWidget {
  final String label;
  final VoidCallback onTap;

  const _SuggestionChip({required this.label, required this.onTap});

  @override
  State<_SuggestionChip> createState() => _SuggestionChipState();
}

class _SuggestionChipState extends State<_SuggestionChip> {
  bool _hover = false;

  @override
  Widget build(BuildContext context) {
    return MouseRegion(
      onEnter: (_) => setState(() => _hover = true),
      onExit: (_) => setState(() => _hover = false),
      cursor: SystemMouseCursors.click,
      child: GestureDetector(
        onTap: widget.onTap,
        child: AnimatedContainer(
          duration: const Duration(milliseconds: 160),
          padding: const EdgeInsets.symmetric(horizontal: 1, vertical: 1),
          decoration: BoxDecoration(
            gradient: _hover ? kAiGradient : kAiGradientMuted,
            borderRadius: BorderRadius.circular(16),
          ),
          child: Container(
            padding:
                const EdgeInsets.symmetric(horizontal: 11, vertical: 6),
            decoration: BoxDecoration(
              color: _hover
                  ? const Color(0xFF1A1530)
                  : const Color(0xFF15101F),
              borderRadius: BorderRadius.circular(15),
            ),
            child: ShaderMask(
              shaderCallback: (bounds) =>
                  kAiGradient.createShader(bounds),
              blendMode: BlendMode.srcIn,
              child: Text(
                widget.label,
                style: GoogleFonts.sora(
                  color: Colors.white,
                  fontSize: 11,
                  fontWeight: FontWeight.w500,
                ),
              ),
            ),
          ),
        ),
      ),
    );
  }
}

// ═════════════════════════════════════════════════════════
//  Private Widget: Glass Icon Button
// ═════════════════════════════════════════════════════════

class _GlassIconButton extends StatelessWidget {
  final IconData icon;
  final String tooltip;
  final VoidCallback? onTap;

  const _GlassIconButton({
    required this.icon,
    required this.tooltip,
    this.onTap,
  });

  @override
  Widget build(BuildContext context) {
    return Tooltip(
      message: tooltip,
      child: GestureDetector(
        onTap: onTap,
        child: Container(
          width: 34,
          height: 34,
          decoration: BoxDecoration(
            color: _cardBg,
            shape: BoxShape.circle,
            border: Border.all(color: _cardBorder),
          ),
          child: Icon(icon, size: 16, color: _textMuted),
        ),
      ),
    );
  }
}

// ═════════════════════════════════════════════════════════
//  Private Widget: Confirm Sheet (generic)
// ═════════════════════════════════════════════════════════

class _ConfirmSheet extends StatelessWidget {
  final String title;
  final String message;
  final String confirmLabel;
  final VoidCallback onConfirm;

  const _ConfirmSheet({
    required this.title,
    required this.message,
    required this.confirmLabel,
    required this.onConfirm,
  });

  @override
  Widget build(BuildContext context) {
    return Container(
      padding: const EdgeInsets.all(24),
      decoration: const BoxDecoration(
        color: _sheetBg,
        borderRadius: BorderRadius.vertical(top: Radius.circular(24)),
        border: Border(top: BorderSide(color: _cardBorder)),
      ),
      child: Column(
        mainAxisSize: MainAxisSize.min,
        children: [
          // Handle
          Center(
            child: Container(
              width: 40,
              height: 4,
              decoration: BoxDecoration(
                color: _textMuted.withValues(alpha: 0.3),
                borderRadius: BorderRadius.circular(2),
              ),
            ),
          ),
          const SizedBox(height: 20),
          Text(
            title,
            style: GoogleFonts.instrumentSerif(
              color: _textGhost,
              fontSize: 20,
              fontWeight: FontWeight.bold,
            ),
          ),
          const SizedBox(height: 12),
          Text(
            message,
            textAlign: TextAlign.center,
            style: GoogleFonts.sora(color: _textMuted, fontSize: 13),
          ),
          const SizedBox(height: 24),
          Row(
            children: [
              Expanded(
                child: GestureDetector(
                  onTap: () => Navigator.pop(context),
                  child: Container(
                    padding: const EdgeInsets.symmetric(vertical: 14),
                    decoration: BoxDecoration(
                      borderRadius: BorderRadius.circular(12),
                      border: Border.all(color: _cardBorder),
                    ),
                    child: Center(
                      child: Text(
                        AppLocalizations.of(context).aiChatCancel,
                        style: GoogleFonts.sora(
                          color: _textMuted,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                    ),
                  ),
                ),
              ),
              const SizedBox(width: 12),
              Expanded(
                child: GestureDetector(
                  onTap: onConfirm,
                  child: Container(
                    padding: const EdgeInsets.symmetric(vertical: 14),
                    decoration: BoxDecoration(
                      gradient: const LinearGradient(
                        colors: [_accent, Color(0xFF9F7AEA)],
                      ),
                      borderRadius: BorderRadius.circular(12),
                    ),
                    child: Center(
                      child: Text(
                        confirmLabel,
                        style: GoogleFonts.sora(
                          color: _textGhost,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                    ),
                  ),
                ),
              ),
            ],
          ),
        ],
      ),
    );
  }
}
