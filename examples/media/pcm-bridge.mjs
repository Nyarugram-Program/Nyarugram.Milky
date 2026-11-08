// Auxiliary audio component for a framework bot: opens the Nyarugram media
// WebSocket for one call, streams 20 ms PCM frames and records the peer audio.
//
// A NoneBot plugin cannot carry audio by itself, so run this next to the bot:
//   MILKY_BASE=http://127.0.0.1:8090 MILKY_TOKEN=... CALL_ID=... \
//   node examples/media/pcm-bridge.mjs
// Optional: INPUT_WAV input.wav OUTPUT_WAV received.wav TONE_HZ=440 SECONDS=10
import { readFileSync, writeFileSync } from 'node:fs';

const base = process.env.MILKY_BASE ?? 'http://127.0.0.1:8090';
const token = process.env.MILKY_TOKEN ?? '';
const callId = process.env.CALL_ID ?? '';
const outputPath = process.env.OUTPUT_WAV ?? 'received.wav';
const toneHz = Number(process.env.TONE_HZ ?? 440);
const maxSeconds = Number(process.env.SECONDS ?? 10);

if (!token || !callId) {
  console.error('MILKY_TOKEN and CALL_ID are required');
  process.exit(2);
}

async function api(action, body) {
  const response = await fetch(`${base}/api/${action}`, {
    method: 'POST',
    headers: { 'content-type': 'application/json', authorization: `Bearer ${token}` },
    body: JSON.stringify(body ?? {}),
  });
  const value = await response.json();
  if (value.retcode !== 0) throw new Error(`${action}: ${value.retcode} ${value.message}`);
  return value.data;
}

function readWav(path) {
  const buffer = readFileSync(path);
  const channels = buffer.readUInt16LE(22);
  const rate = buffer.readUInt32LE(24);
  const bits = buffer.readUInt16LE(34);
  if (channels !== 1 || rate !== 48000 || bits !== 16) {
    throw new Error(`INPUT_WAV must be mono 48 kHz 16-bit, got ${channels}ch ${rate}Hz ${bits}bit`);
  }
  const data = buffer.subarray(44);
  return data.subarray(0, data.length - (data.length % 2));
}

function frame(sequence, samples) {
  const header = Buffer.alloc(16);
  header[0] = 0x4e; header[1] = 0x4d; header[2] = 1; header[3] = 1;
  header.writeUInt32BE(sequence >>> 0, 4);
  header.writeBigUInt64BE(BigInt(Date.now()), 8);
  return Buffer.concat([header, samples]);
}

function wavHeader(sampleCount) {
  const header = Buffer.alloc(44);
  header.write('RIFF', 0);
  header.writeUInt32LE(36 + sampleCount * 2, 4);
  header.write('WAVEfmt ', 8);
  header.writeUInt32LE(16, 16);
  header.writeUInt16LE(1, 20);
  header.writeUInt16LE(1, 22);
  header.writeUInt32LE(48000, 24);
  header.writeUInt32LE(48000 * 2, 28);
  header.writeUInt16LE(2, 32);
  header.writeUInt16LE(16, 34);
  header.write('data', 36);
  header.writeUInt32LE(sampleCount * 2, 40);
  return header;
}

const opened = await api('nyarugram_open_call_media', { call_id: callId, input: true, output: true });
const socket = new WebSocket(`${base}${opened.url_path}?access_token=${opened.token}`);
socket.binaryType = 'arraybuffer';
const received = [];

socket.addEventListener('message', (event) => {
  if (typeof event.data === 'string') return;
  const buffer = Buffer.from(event.data);
  if (buffer.length < 16 || buffer[0] !== 0x4e || buffer[1] !== 0x4d) return;
  if ((buffer[3] & 1) === 0) received.push(buffer.subarray(16));
});

await new Promise((resolve, reject) => {
  socket.addEventListener('open', resolve, { once: true });
  socket.addEventListener('error', reject, { once: true });
});

const input = process.env.INPUT_WAV ? readWav(process.env.INPUT_WAV) : null;
let sequence = 0;
let phase = 0;
let sentSamples = 0;
const deadline = Date.now() + maxSeconds * 1000;

while (Date.now() < deadline) {
  let samples;
  if (input) {
    if (sentSamples >= input.length) break;
    samples = input.subarray(sentSamples, Math.min(sentSamples + 1920, input.length));
    sentSamples += samples.length;
  } else {
    samples = Buffer.alloc(1920);
    for (let i = 0; i < 960; i++) {
      samples.writeInt16LE(Math.round(3000 * Math.sin(2 * Math.PI * toneHz * (phase + i) / 48000)), i * 2);
    }
    phase += 960;
  }
  socket.send(frame(sequence++, samples));
  await new Promise((resolve) => setTimeout(resolve, 20));
}

socket.close();
const output = Buffer.concat([wavHeader(received.reduce((sum, chunk) => sum + chunk.length / 2, 0)), ...received]);
writeFileSync(outputPath, output);
console.log(JSON.stringify({ ok: true, call_id: callId, frames_sent: sequence, samples_received: output.length / 2 - 22, output: outputPath }));
