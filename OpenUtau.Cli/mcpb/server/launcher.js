#!/usr/bin/env node
// Starts the MCP server of an installed OpenUtau Chiaki; the server itself is not in this bundle.
const { spawn } = require('child_process');
const fs = require('fs');
const os = require('os');
const path = require('path');

function option(name) {
  const prefix = `--${name}=`;
  const arg = process.argv.slice(2).find(a => a.startsWith(prefix));
  const value = arg ? arg.slice(prefix.length).trim() : '';
  // An unset optional setting can arrive as the unexpanded placeholder.
  return value.startsWith('${') ? '' : value;
}

function candidates() {
  const exe = process.platform === 'win32' ? 'OpenUtau.Cli.exe' : 'OpenUtau.Cli';
  const home = os.homedir();
  const list = [option('cli'), process.env.OPENUTAU_CLI];
  if (process.platform === 'darwin') {
    list.push(`/Applications/OpenUtau-Chiaki.app/Contents/MacOS/${exe}`);
    list.push(path.join(home, 'Applications', 'OpenUtau-Chiaki.app', 'Contents', 'MacOS', exe));
  } else if (process.platform === 'win32') {
    const local = process.env.LOCALAPPDATA || path.join(home, 'AppData', 'Local');
    list.push(path.join(local, 'OpenUtau.Chiaki', 'current', exe));
  }
  return list.filter(Boolean);
}

const tried = candidates().map(p => path.resolve(p));
const cli = tried.find(p => fs.existsSync(p) && fs.statSync(p).isFile());
if (!cli) {
  const where = tried.length ? ` Looked in: ${tried.join(', ')}.` : '';
  console.error(
    'OpenUtau.Cli was not found. Install OpenUtau Chiaki (a release that has OpenUtau.Cli next to the app), ' +
    `or set "OpenUtau.Cli location" in this extension's settings.${where}`);
  process.exit(1);
}

const args = ['mcp'];
if (option('gui') === 'true') {
  args.push('--gui');
}
const child = spawn(cli, args, { stdio: 'inherit', windowsHide: true });
child.on('error', e => {
  console.error(`Could not start ${cli}: ${e.message}`);
  process.exit(1);
});
child.on('exit', (code, signal) => process.exit(signal ? 1 : code ?? 1));
for (const signal of ['SIGINT', 'SIGTERM']) {
  process.on(signal, () => child.kill(signal));
}
