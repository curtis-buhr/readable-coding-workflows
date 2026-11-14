export const responsesModel = 'gpt-6.1-sol';
export const reasoningEffort = 'xhigh';

const nanodollarsPerDollar = 1_000_000_000;
const longContextThreshold = 272_000;
const tokenCount = value => Number.isSafeInteger(value) && value >= 0;

/** Holds a request's maximum estimated model cost until its usage is known. */
export function createBudget(maxUsd) {
  const maximum = Math.floor(maxUsd * nanodollarsPerDollar);
  if (typeof maxUsd !== 'number' || !Number.isSafeInteger(maximum) || maximum <= 0) {
    throw new RangeError('The budget must be a positive dollar amount.');
  }

  let committed = 0;
  return {
    get spentUsd() { return committed / nanodollarsPerDollar; },
    get remainingUsd() { return Math.max(0, maximum - committed) / nanodollarsPerDollar; },
    reserve(nanodollars) {
      if (!Number.isSafeInteger(nanodollars) || nanodollars < 0 || committed + nanodollars > maximum) {
        return null;
      }
      committed += nanodollars;
      let settled = false;
      return actual => {
        if (settled) throw new Error('A budget reservation can only settle once.');
        settled = true;
        // A failed transport can hide billed usage. Keep its full reservation.
        const charged = actual ?? nanodollars;
        committed += charged - nanodollars;
        return charged / nanodollarsPerDollar;
      };
    },
  };
}

/** Creates an authenticated transport without forwarding credentials to tools. */
export function createResponsesTransport({ apiKey, projectId, organizationId, fetchImpl = fetch }) {
  if (typeof apiKey !== 'string' || !apiKey.trim()) {
    throw new Error('Set OPENAI_API_KEY to a key from the benchmark API project.');
  }
  const headers = {
    Authorization: `Bearer ${apiKey}`,
    'Content-Type': 'application/json',
    ...(projectId ? { 'OpenAI-Project': projectId } : {}),
    ...(organizationId ? { 'OpenAI-Organization': organizationId } : {}),
  };

  async function post(path, body, signal) {
    const response = await fetchImpl(`https://api.openai.com/v1/responses${path}`, {
      method: 'POST',
      headers,
      body: JSON.stringify(body),
      signal,
      redirect: 'error',
    });
    if (response.ok) return response.json();

    const payload = await response.json().catch(() => null);
    // Retain the status and machine code without logging server messages or request data.
    const error = new Error('Responses API request failed.');
    error.httpStatus = response.status;
    error.code = safeCode(payload?.error?.code);
    throw error;
  }

  return {
    async countInput(body, { signal }) {
      const { model, instructions, input, tools, reasoning, parallel_tool_calls, truncation } = body;
      const counted = await post('/input_tokens', {
        model, instructions, input, tools, reasoning, parallel_tool_calls, truncation,
      }, signal);
      return counted.input_tokens;
    },
    create(body, { signal }) { return post('', body, signal); },
  };
}

function safeCode(value) {
  return typeof value === 'string' && /^[a-z][a-z0-9_]{0,63}$/.test(value) ? value : null;
}

function readUsage(value) {
  if (!tokenCount(value?.input_tokens) || !tokenCount(value?.output_tokens)) return null;
  const cached = value.input_tokens_details?.cached_tokens;
  const written = value.input_tokens_details?.cache_write_tokens;
  const writtenLong = value.input_tokens_details?.cache_write_tokens_details?.ttl_12h_tokens;
  const reasoning = value.output_tokens_details?.reasoning_tokens;
  return {
    inputTokens: value.input_tokens,
    outputTokens: value.output_tokens,
    cachedInputTokens: tokenCount(cached) ? cached : null,
    cacheWriteTokens: tokenCount(written) ? written : null,
    cacheWriteLongTokens: tokenCount(writtenLong) ? writtenLong : null,
    reasoningTokens: tokenCount(reasoning) ? reasoning : null,
    totalTokens: value.input_tokens + value.output_tokens,
  };
}

function rates(inputTokens) {
  // Prices per token in nanodollars, at the standard global GPT-6.1 Sol rate.
  return inputTokens > longContextThreshold
    ? { uncached: 4000, cached: 200, write: 5000, writeLong: 10000, output: 15000 }
    : { uncached: 2000, cached: 100, write: 2500, writeLong: 5000, output: 10000 };
}

/** Estimates the model charge when the API supplies the necessary token counters. */
export function estimateRequestCost(usage) {
  const input = usage?.inputTokens;
  const output = usage?.outputTokens;
  const cached = usage?.cachedInputTokens;
  const written = usage?.cacheWriteTokens;
  // The request selects the 30-minute cache lifetime. Honor a 12-hour counter when present.
  const longWrites = usage?.cacheWriteLongTokens ?? 0;
  if (![input, output, cached, written, longWrites].every(tokenCount) ||
      cached + written > input || longWrites > written) return null;

  const price = rates(input);
  return ((input - cached - written) * price.uncached + cached * price.cached +
    (written - longWrites) * price.write + longWrites * price.writeLong +
    output * price.output) / nanodollarsPerDollar;
}

function waitFor(operation, signal) {
  signal.throwIfAborted();
  return new Promise((resolve, reject) => {
    const abort = () => reject(signal.reason);
    signal.addEventListener('abort', abort, { once: true });
    Promise.resolve().then(() => {
      signal.throwIfAborted();
      return operation();
    }).then(resolve, reject).finally(() => signal.removeEventListener('abort', abort));
  });
}

function functionCall(item, toolNames, seen) {
  if (typeof item.call_id !== 'string' || !item.call_id || seen.has(item.call_id) ||
      !toolNames.has(item.name) || typeof item.arguments !== 'string') return null;

  try {
    const args = JSON.parse(item.arguments);
    if (!args || typeof args !== 'object' || Array.isArray(args)) return null;
    return { item, args };
  } catch {
    return null;
  }
}

/** Runs one fresh candidate conversation with a shared experiment budget. */
export async function runResponses({
  transport, instructions, input, tools, executeTool, limits, budget, onRequest, onTool, signal,
}) {
  const { maxCalls, maxInputTokens, maxOutputTokens, timeoutMs, maxToolOutputBytes = 16_384 } = limits;
  if (![maxCalls, maxInputTokens, maxOutputTokens, timeoutMs, maxToolOutputBytes]
    .every(value => Number.isSafeInteger(value) && value > 0) ||
      maxInputTokens > 1_050_000 || maxOutputTokens < 16 || maxOutputTokens > 128_000 ||
      timeoutMs > 2_147_483_647) {
    throw new RangeError('Invalid Responses API run limits.');
  }
  const names = tools.map(tool => tool.name);
  const toolNames = new Set(names);
  if (typeof input !== 'string' || typeof instructions !== 'string' ||
      tools.some(tool => tool.type !== 'function' || typeof tool.name !== 'string' || !tool.name) ||
      names.length !== toolNames.size) {
    throw new TypeError('Provide text instructions, text input, and unique function tools.');
  }

  const started = performance.now();
  const deadline = new AbortController();
  const timer = setTimeout(() => deadline.abort(), timeoutMs);
  const active = signal ? AbortSignal.any([signal, deadline.signal]) : deadline.signal;
  // Opaque reasoning items live only in this history and are passed back unchanged.
  const history = [{ role: 'user', content: input }];
  const requests = [];
  const seenCalls = new Set();
  let toolsCalled = 0;
  let countInputCalls = 0;
  let model = responsesModel;

  function stop(status, reason, details = {}) {
    const budgetUsd = requests.reduce((sum, item) => sum + item.budgetChargedUsd, 0);
    const estimatedCostUsd = requests.every(item => item.estimatedCostUsd !== null)
      ? requests.reduce((sum, item) => sum + item.estimatedCostUsd, 0)
      : null;
    return {
      status, reason, model, requests, toolsCalled, countInputCalls,
      elapsedMs: performance.now() - started, budgetUsd, estimatedCostUsd, ...details,
    };
  }

  function record(request) {
    requests.push(request);
    onRequest?.(request);
  }

  function failure(error, phase) {
    if (deadline.signal.aborted) return stop('timeout', 'deadline');
    if (signal?.aborted) return stop('cancelled', 'external_abort');
    return stop('api_error', phase, {
      errorCode: safeCode(error?.code),
      httpStatus: Number.isInteger(error?.httpStatus) ? error.httpStatus : null,
    });
  }

  try {
    for (let index = 1; index <= maxCalls; index++) {
      const body = {
        model: responsesModel,
        instructions,
        input: [...history],
        tools,
        reasoning: { effort: reasoningEffort },
        service_tier: 'default',
        prompt_cache_options: { ttl: '30m' },
        store: false,
        parallel_tool_calls: false,
        truncation: 'disabled',
        max_output_tokens: maxOutputTokens,
      };

      // Count the fully rendered request before admitting a potentially billable generation.
      let inputTokens;
      try {
        active.throwIfAborted();
        countInputCalls++;
        inputTokens = await waitFor(() => transport.countInput(body, { signal: active }), active);
      } catch (error) {
        return failure(error, 'count_input');
      }
      if (!tokenCount(inputTokens)) return stop('protocol_error', 'invalid_input_count');
      if (inputTokens > maxInputTokens) return stop('limit', 'input_tokens');

      // Reserve from configured caps to cover count discrepancies and unknown response usage.
      const price = rates(maxInputTokens);
      const reserve = maxInputTokens * price.write + maxOutputTokens * price.output;
      const settle = budget.reserve(reserve);
      if (!settle) return stop('limit', 'budget');

      const requestStarted = performance.now();
      let response;
      try {
        response = await waitFor(() => transport.create(body, { signal: active }), active);
      } catch (error) {
        record({
          index, elapsedMs: performance.now() - requestStarted, inputTokenCount: inputTokens,
          outputTokenLimit: maxOutputTokens, reservedUsd: reserve / nanodollarsPerDollar,
          budgetChargedUsd: settle(null), estimatedCostUsd: null, usage: null,
          model: null, serviceTier: null, status: 'error', errorCode: safeCode(error?.code),
        });
        return failure(error, 'create_response');
      }

      const usage = readUsage(response?.usage);
      const tier = response?.service_tier ?? null;
      const servedModel = response?.model;
      const sameModel = servedModel === responsesModel ||
        (typeof servedModel === 'string' && /^gpt-6\.1-sol-\d{4}-\d{2}-\d{2}$/.test(servedModel));
      const estimate = sameModel && tier === 'default' ? estimateRequestCost(usage) : null;
      if (sameModel) model = servedModel;
      const budgetChargedUsd = settle(estimate === null ? null : Math.round(estimate * nanodollarsPerDollar));
      record({
        index, elapsedMs: performance.now() - requestStarted, inputTokenCount: inputTokens,
        outputTokenLimit: maxOutputTokens, reservedUsd: reserve / nanodollarsPerDollar,
        budgetChargedUsd, estimatedCostUsd: estimate, usage, model: sameModel ? servedModel : null,
        serviceTier: safeCode(tier), status: safeCode(response?.status) ?? 'unknown',
        errorCode: safeCode(response?.error?.code),
      });

      if (!sameModel || (tier !== null && tier !== 'default')) return stop('protocol_error', 'unexpected_model_or_tier');
      if (usage && (usage.inputTokens > maxInputTokens || usage.outputTokens > maxOutputTokens)) {
        return stop('limit', 'observed_token_limit');
      }
      if (response.status === 'incomplete' && response.incomplete_details?.reason === 'max_output_tokens') {
        return stop('limit', 'output_tokens');
      }
      if (response.status === 'incomplete' && response.incomplete_details?.reason === 'content_filter') {
        return stop('refused', 'content_filter');
      }
      if (response.status === 'failed') return stop('api_error', 'response_failed', {
        errorCode: safeCode(response.error?.code),
      });
      if (response.status !== 'completed' || !Array.isArray(response.output) ||
          response.output.some(item => !item || !['reasoning', 'function_call', 'message'].includes(item.type))) {
        return stop('protocol_error', 'unexpected_response');
      }

      const calls = response.output.filter(item => item.type === 'function_call');
      const messages = response.output.filter(item => item.type === 'message');
      if (messages.some(message => !Array.isArray(message.content))) {
        return stop('protocol_error', 'invalid_message');
      }
      if (messages.some(message => message.content.some(part => part?.type === 'refusal'))) {
        return stop('refused', 'model_refusal');
      }
      if (calls.length === 0) return messages.some(message =>
        message.content.some(part => part?.type === 'output_text' && typeof part.text === 'string' && part.text.trim()))
        ? stop('completed', 'final_message') : stop('protocol_error', 'missing_final_message');
      if (calls.length > 1) return stop('protocol_error', 'unexpected_parallel_tools');
      const call = functionCall(calls[0], toolNames, seenCalls);
      if (!call) return stop('protocol_error', 'invalid_tool_call');
      if (index === maxCalls) return stop('limit', 'request_count');
      seenCalls.add(call.item.call_id);

      // Only the caller's sandbox callback executes a tool, using the declared name and arguments.
      const toolStarted = performance.now();
      toolsCalled++;
      let output;
      try {
        const result = await waitFor(() => executeTool(call.item.name, call.args, { signal: active }), active);
        output = typeof result === 'string' ? result : JSON.stringify(result);
        if (typeof output !== 'string') return stop('protocol_error', 'invalid_tool_output');
        if (Buffer.byteLength(output) > maxToolOutputBytes) {
          output = JSON.stringify({ error: 'tool_output_limit' });
        }
      } catch {
        onTool?.({ name: call.item.name, elapsedMs: performance.now() - toolStarted, status: 'error' });
        if (active.aborted) return failure(null, 'execute_tool');
        return stop('tool_error', 'execute_tool');
      }
      onTool?.({ name: call.item.name, elapsedMs: performance.now() - toolStarted, status: 'completed' });
      history.push(...response.output, { type: 'function_call_output', call_id: call.item.call_id, output });
    }
    return stop('limit', 'request_count');
  } finally {
    clearTimeout(timer);
  }
}
