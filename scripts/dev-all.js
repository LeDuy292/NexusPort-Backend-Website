const { spawn } = require('node:child_process');
const { createInterface } = require('node:readline');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const npmCli = process.env.npm_execpath;

if (!npmCli) {
  console.error('Run this script through npm: npm run dev:all');
  process.exit(1);
}

const services = [
  { name: 'JS API', cwd: root, args: ['run', 'dev'] },
  { name: 'Node Core', cwd: path.join(root, 'node-core'), args: ['run', 'dev'] },
  { name: 'C# Core', cwd: root, args: ['run', 'dev:csharp'] },
  { name: 'AI Service', cwd: root, args: ['run', 'dev:aiservice'] },
];

const children = [];
let stopping = false;

function stopChild(child) {
  if (!child.pid || child.exitCode !== null || child.signalCode !== null) return;

  if (process.platform === 'win32') {
    spawn('taskkill', ['/PID', String(child.pid), '/T', '/F'], { stdio: 'ignore' });
  } else {
    try {
      process.kill(-child.pid, 'SIGTERM');
    } catch (error) {
      if (error.code !== 'ESRCH') console.error(error);
    }
  }
}

function stopAll(exitCode = 0) {
  if (stopping) return;
  stopping = true;
  process.exitCode = exitCode;
  for (const child of children) stopChild(child);
}

for (const service of services) {
  const child = spawn(process.execPath, [npmCli, ...service.args], {
    cwd: service.cwd,
    env: process.env,
    stdio: ['inherit', 'pipe', 'pipe'],
    detached: process.platform !== 'win32',
  });
  children.push(child);

  for (const stream of [child.stdout, child.stderr]) {
    createInterface({ input: stream }).on('line', (line) => {
      console.log(`[${service.name}] ${line}`);
    });
  }

  child.on('error', (error) => {
    console.error(`[${service.name}] Could not start: ${error.message}`);
    stopAll(1);
  });

  child.on('exit', (code, signal) => {
    if (!stopping) {
      console.error(`[${service.name}] stopped${signal ? ` (${signal})` : ` with exit code ${code}`}`);
      stopAll(code || 1);
    }
  });
}

process.on('SIGINT', () => stopAll(0));
process.on('SIGTERM', () => stopAll(0));
