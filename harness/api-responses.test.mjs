import assert from 'node:assert/strict';
import test from 'node:test';
import {
  createBudget, createResponsesTransport, estimateRequestCost, reasoningEffort, responsesModel, runResponses,
} from './api-responses.mjs';

const tools = [{
  type: 'function',
  name: 'read_file',
  description: 'Read a file in the candidate workspace.',
  parameters: { type: 'object', properties: { path: { type: 'string' } }, required: ['path'], additionalProperties: false },
  strict: true,
}];

const usage = {
  input_tokens: 100,
  input_tokens_details: { cached_tokens: 40, cache_write_tokens: 10 },
  output_tokens: 30,
  output_tokens_details: { reasoning_tokens: 20 },
};
const message = {
  type: 'message',
  role: 'assistant',
  content: [{ type: 'output_text', text: 'Done.' }],
};
const reply = output => ({
  model: responsesModel, service_tier: 'default', status: 'completed', output, usage,
});
const read = {
  type: 'function_call', name: 'read_file', call_id: 'call_1',
  arguments: '{"path":"Sync.cs"}',
};

function fixture(transport, overrides = {}) {
  const defaultLimits = { maxCalls: 3, maxInputTokens: 500, maxOutputTokens: 100, timeoutMs: 2000 };
  const { limits, ...rest } = overrides;
  return runResponses({
    transport,
    instructions: 'Work in your candidate workspace.',
    input: 'Maintain the synchronization code.',
    tools,
    async executeTool() { return 'content'; },
    budget: createBudget(1),
    ...rest,
    limits: { ...defaultLimits, ...limits },
  });
}

test('preserves the tool and reasoning history within a run without including them in telemetry', async () => {
  const reasoning = { type: 'reasoning', id: 'rs_1', encrypted_content: 'opaque', summary: [] };
  const requests = [];
  const events = [];
  let counts = 0;
  const result = await fixture({
    async countInput(body) {
      assert.equal(body.model, responsesModel);
      counts++;
      return 100;
    },
    async create(body) {
      requests.push(body);
      return requests.length === 1 ? reply([reasoning, read]) : reply([message]);
    },
  }, {
    executeTool(name, args) {
      assert.equal(name, 'read_file');
      assert.deepEqual(args, { path: 'Sync.cs' });
      return { content: 'candidate code' };
    },
    onRequest: item => events.push(item),
  });

  assert.equal(result.status, 'completed');
  assert.equal(result.toolsCalled, 1);
  assert.equal(result.countInputCalls, 2);
  assert.equal(counts, 2);
  assert.equal(events.length, 2);
  assert.equal(requests[0].store, false);
  assert.equal(requests[0].service_tier, 'default');
  assert.equal(requests[0].reasoning.effort, reasoningEffort);
  assert.equal(requests[0].input.length, 1);
  assert.deepEqual(requests[1].input.slice(1), [reasoning, read, {
    type: 'function_call_output', call_id: 'call_1', output: '{"content":"candidate code"}',
  }]);
  assert.equal(result.requests[0].usage.reasoningTokens, 20);
  assert.equal(result.estimatedCostUsd, 0.000858);
  assert.equal(result.budgetUsd, result.estimatedCostUsd);
  assert.doesNotMatch(JSON.stringify(result), /opaque|candidate code|Sync\.cs|Done\./);
});

test('rejects the generation before admission if the request exceeds input or spend limits', async () => {
  const oversized = await fixture({
    async countInput() { return 501; },
    async create() { assert.fail('Oversized generation must never launch'); },
  });
  assert.equal(oversized.reason, 'input_tokens');
  assert.equal(oversized.requests.length, 0);
  const overBudget = await fixture({
    async countInput() { return 100; },
    async create() { assert.fail('Unaffordable generation must never launch'); },
  }, { budget: createBudget(0.001) });
  assert.equal(overBudget.reason, 'budget');
  assert.equal(overBudget.budgetUsd, 0);
});

test('holds the full reservation on unknown transport usage and never retries', async () => {
  let calls = 0;
  const budget = createBudget(0.0025);
  const failed = await fixture({
    async countInput() { return 100; },
    async create() {
      calls++;
      throw Object.assign(new Error('private server details'), { httpStatus: 429, code: 'rate_limit_exceeded' });
    },
  }, { budget });
  assert.equal(calls, 1);
  assert.equal(failed.status, 'api_error');
  assert.equal(failed.errorCode, 'rate_limit_exceeded');
  assert.equal(failed.requests[0].usage, null);
  assert.equal(failed.estimatedCostUsd, null);
  assert.equal(failed.budgetUsd, 0.00225);
  assert.equal(budget.remainingUsd, 0.00025);
  assert.doesNotMatch(JSON.stringify(failed), /private server/);
});

test('rejects unknown or repeated tool calls and stops at the request limit', async () => {
  let calls = 0;
  const repeated = await fixture({
    async countInput() { return 100; },
    async create() { return reply([read]); },
  }, { executeTool() { calls++; return ''; } });
  assert.equal(repeated.reason, 'invalid_tool_call');
  assert.equal(calls, 1);
  const last = await fixture({
    async countInput() { return 100; },
    async create() { return reply([read]); },
  }, { limits: { maxCalls: 1 }, executeTool() { assert.fail('No next generation remains to consume the tool result'); } });
  assert.equal(last.reason, 'request_count');
  const unknown = await fixture({
    async countInput() { return 100; },
    async create() { return reply([{ ...read, name: 'not_a_declared_tool' }]); },
  }, { executeTool() { assert.fail('Undeclared tools must never execute'); } });
  assert.equal(unknown.reason, 'invalid_tool_call');
});

test('stops a pending generation at the whole-attempt deadline and accounts for unknown usage', async () => {
  const result = await fixture({
    async countInput() { return 100; },
    create(_body, { signal }) {
      assert.equal(signal.aborted, false);
      return new Promise(() => {});
    },
  }, { limits: { timeoutMs: 25 } });
  assert.equal(result.status, 'timeout');
  assert.equal(result.reason, 'deadline');
  assert.equal(result.requests[0].usage, null);
  assert.equal(result.budgetUsd, 0.00225);
});

test('accounts for an output-limited response without applying partial edits', async () => {
  const result = await fixture({
    async countInput() { return 100; },
    async create() {
      return { ...reply([read]), status: 'incomplete', incomplete_details: { reason: 'max_output_tokens' } };
    },
  }, { executeTool() { assert.fail('An incomplete response cannot apply a tool call'); } });
  assert.equal(result.status, 'limit');
  assert.equal(result.reason, 'output_tokens');
  assert.equal(result.requests[0].usage.totalTokens, 130);
  assert.equal(result.estimatedCostUsd, 0.000429);
});

test('a concurrent run cannot spend the same reservation twice', async () => {
  const budget = createBudget(0.003);
  let creations = 0;
  let finish;
  const pending = new Promise(resolve => { finish = resolve; });
  const transport = {
    async countInput() { return 100; },
    async create() { creations++; return pending; },
  };
  const first = fixture(transport, { budget });
  const second = await fixture(transport, { budget });
  assert.equal(second.reason, 'budget');
  assert.equal(creations, 1);
  finish(reply([message]));
  assert.equal((await first).status, 'completed');
  assert.equal(budget.spentUsd, 0.000429);
});

test('prices cache writes and applies the long-context threshold per request', () => {
  assert.equal(estimateRequestCost({
    inputTokens: 100_000, outputTokens: 1_000, cachedInputTokens: 0, cacheWriteTokens: 0,
  }), 0.21);
  assert.equal(estimateRequestCost({
    inputTokens: 300_000, outputTokens: 1_000, cachedInputTokens: 100_000, cacheWriteTokens: 150_000,
    cacheWriteLongTokens: 50_000,
  }), 1.235);
  assert.equal(estimateRequestCost({ inputTokens: 10, outputTokens: 2 }), null);
  assert.equal(estimateRequestCost({
    inputTokens: 10, outputTokens: 2, cachedInputTokens: 10, cacheWriteTokens: 10,
  }), null);
});

test('sends credentials only to official endpoints and strips private API error text', async () => {
  const calls = [];
  const transport = createResponsesTransport({
    apiKey: 'mock-key', projectId: 'proj_mock',
    async fetchImpl(url, options) {
      calls.push({ url, options, body: JSON.parse(options.body) });
      if (url.endsWith('/input_tokens')) return Response.json({ input_tokens: 100 });
      return Response.json({ error: { code: 'invalid_request_error', message: 'private' } }, { status: 400 });
    },
  });
  const result = await fixture(transport);
  assert.deepEqual(calls.map(call => call.url), [
    'https://api.openai.com/v1/responses/input_tokens', 'https://api.openai.com/v1/responses',
  ]);
  assert.equal(calls[0].options.headers.Authorization, 'Bearer mock-key');
  assert.equal(calls[0].options.headers['OpenAI-Project'], 'proj_mock');
  assert.equal(calls[0].options.redirect, 'error');
  assert.equal(calls[0].body.store, undefined);
  assert.equal(calls[1].body.store, false);
  assert.equal(result.errorCode, 'invalid_request_error');
  assert.doesNotMatch(JSON.stringify(result), /mock-key|private/);
});
