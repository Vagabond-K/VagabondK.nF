'use strict';

const assert = require('node:assert/strict');
const dgram = require('node:dgram');
const readline = require('node:readline');
const sparkplug = require('sparkplug-client');

const [host, board] = process.argv.slice(2);
assert(host && board, 'Usage: node peer.js hostIPv4 boardIPv4');
const prefix = 'spBv1.0/VKNF';
const udp = dgram.createSocket('udp4');
const input = readline.createInterface({ input: process.stdin });
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
let failure, stopped = false, commandId = 0, pending, sequence = -1;
let births = 0, data = 0, deaths = 0, nodeDeaths = 0, rebirths = 0;
const number = value => Number(value.toString());
const client = sparkplug.newClient({
    serverUrl: `mqtt://${host}:18883`, username: '', password: '',
    groupId: 'VKNF', edgeNode: 'edge-a', clientId: 'vknf-tahu-interop',
    version: 'spBv1.0', publishDeath: true, mqttOptions: { reconnectPeriod: 0 }
});

function healthy() { if (failure) throw failure; }
function guard(action) {
    return (...args) => { try { action(...args); } catch (error) { failure = error; } };
}
function birth() {
    client.publishNodeBirth({ timestamp: Date.now(), metrics: [
        { name: 'Node Control/Rebirth', type: 'Boolean', value: false }
    ] });
    client.publishDeviceBirth('sensor-a', { timestamp: Date.now(), metrics: [
        { name: 'Sensor/Running', alias: 10, type: 'Boolean', value: true },
        { name: 'Sensor/Count', alias: 11, type: 'Int32', value: -123 },
        { name: 'Sensor/Temperature', alias: 12, type: 'Float', value: 23.5 },
        { name: 'Sensor/Energy', alias: 13, type: 'Double', value: 123456789.125 }
    ] });
}
client.on('error', error => { failure = error; });
client.on('birth', guard(birth));
client.on('ncmd', guard(payload => {
    assert.equal(payload.metrics.length, 1);
    assert.equal(payload.metrics[0].name, 'Node Control/Rebirth');
    assert.equal(payload.metrics[0].value, true);
    rebirths++;
    birth();
}));
client.on('message', guard((topic, payload) => {
    const verb = topic.split('/')[2];
    if (verb === 'NBIRTH') {
        assert.equal(number(payload.seq), 0);
        assert.equal(payload.metrics.length, 2);
        const bd = payload.metrics.find(m => m.name === 'bdSeq');
        assert.equal(bd.type, 'Int64');
        assert(number(bd.value) >= 200 && number(bd.value) <= 202);
        sequence = 0;
    } else if (verb === 'NDEATH') {
        assert.equal(payload.metrics.length, 1);
        assert.equal(payload.metrics[0].name, 'bdSeq');
        assert(number(payload.metrics[0].value) >= 200);
        sequence = -1;
        nodeDeaths++;
    } else {
        assert(sequence >= 0, 'Birth must precede device messages.');
        assert.equal(number(payload.seq), (sequence + 1) & 255);
        sequence = number(payload.seq);
        if (verb === 'DDEATH') {
            assert.equal(payload.metrics.length, 0);
            deaths++;
        } else {
            assert(verb === 'DBIRTH' || verb === 'DDATA');
            assert.deepEqual(payload.metrics.map(m => number(m.alias)), [10, 11, 12, 13]);
            if (verb === 'DBIRTH') {
                assert.deepEqual(payload.metrics.map(m => m.name),
                    ['Sensor/Running', 'Sensor/Count', 'Sensor/Temperature', 'Sensor/Energy']);
                assert.deepEqual(payload.metrics.map(m => m.type), ['Boolean', 'Int32', 'Float', 'Double']);
                assert.deepEqual(payload.metrics.map(m => m.value), [true, -100, 25.25, 987654321.125]);
                births++;
            } else {
                // Tahu 1.0.3 does not resolve omitted metric types from birth aliases.
                assert(payload.metrics.every(m => m.value === null));
                data++;
            }
        }
    }
}));
udp.on('error', error => { failure = error; });
udp.on('message', guard(bytes => {
    const text = bytes.toString('utf8').split('|');
    assert.equal(text.length, 12, bytes.toString('utf8'));
    assert.equal(text[9], 'OK', bytes.toString('utf8'));
    if (pending && Number(text[0]) === pending.id) pending.resolve(text.map(Number));
}));

async function command(code) {
    const id = ++commandId;
    assert(id <= 255);
    const request = Buffer.from([77, 81, 83, 49, id, code]);
    let response;
    pending = { id, resolve: value => { response = value; } };
    try {
        for (let attempt = 0; attempt < 30; attempt++) {
            healthy();
            await new Promise((resolve, reject) => udp.send(request, error => error ? reject(error) : resolve()));
            for (let n = 0; n < 10 && !response; n++) { await delay(100); healthy(); }
            if (response) return response;
        }
        throw new Error(`Board diagnostic timeout: command ${code}`);
    } finally { pending = undefined; }
}
async function waitState(predicate) {
    const deadline = Date.now() + 10000;
    let state;
    do {
        state = await command(0);
        if (predicate(state)) return state;
        await delay(100);
    } while (Date.now() < deadline);
    throw new Error(`Tahu/board state timeout: ${JSON.stringify(state)}`);
}
async function waitEvent(predicate) {
    for (let i = 0; i < 100; i++) {
        healthy();
        if (predicate()) return;
        await delay(100);
    }
    throw new Error('Tahu receive event timeout.');
}
async function run() {
    await new Promise((resolve, reject) => {
        client.once('connect', () => client.subscribeTopic(`${prefix}/+/edge-b/#`, { qos: 1 }, (error, granted) => {
            if (error) return reject(error);
            try { assert.equal(granted.length, 1); assert.equal(granted[0].qos, 1); resolve(); }
            catch (e) { reject(e); }
        }));
        client.once('error', reject);
    });
    await new Promise(resolve => udp.connect(20005, board, resolve));
    console.log('READY');
    await new Promise(resolve => input.once('line', line => { assert.equal(line, 'go'); resolve(); }));
    await waitState(s => s[3] === 1 && s[8] >= 1);
    await waitEvent(() => births >= 1 && rebirths >= 1);
    birth();
    let state = await waitState(s => s[4] === 1 && s[5] === 1);
    assert.deepEqual([state[6], state[7], state[10], state[11]], [-123, 1, 23.5, 123456789.125]);
    client.publishDeviceData('sensor-a', { timestamp: Date.now(), metrics: [
        { alias: 11, type: 'Int32', value: -456 }, { alias: 12, type: 'Float', value: -12.25 }
    ] });
    state = await waitState(s => s[4] === 1 && s[5] === 2);
    assert.deepEqual([state[6], state[7], state[10], state[11]], [-456, 1, -12.25, 123456789.125]);
    await command(1);
    await waitEvent(() => data === 1);
    client.publishDeviceData('sensor-a', { timestamp: Date.now(), metrics: [
        { alias: 10, type: 'Boolean', value: false }
    ] });
    state = await waitState(s => s[4] === 1 && s[5] === 3);
    assert.equal(state[7], 0);
    await command(1);
    await waitEvent(() => data === 2);
    const received = state[1];
    client.publishDeviceDeath('sensor-a', { timestamp: Date.now(), metrics: [] });
    await waitState(s => s[4] === 0 && s[1] > received && s[2] === 0);
    client.publishDeviceBirth('sensor-a', { timestamp: Date.now(), metrics: [
        { name: 'Sensor/Running', alias: 10, type: 'Boolean', value: false },
        { name: 'Sensor/Count', alias: 11, type: 'Int32', value: 789 },
        { name: 'Sensor/Temperature', alias: 12, type: 'Float', value: 12.5 },
        { name: 'Sensor/Energy', alias: 13, type: 'Double', value: 456.125 }
    ] });
    state = await waitState(s => s[4] === 1 && s[5] === 5);
    assert.deepEqual([state[6], state[7], state[10], state[11]], [789, 0, 12.5, 456.125]);
    const before = nodeDeaths;
    await command(4);
    await waitEvent(() => nodeDeaths === before + 1);
    await command(3);
    await waitEvent(() => births >= 2);
    await command(2);
    await waitEvent(() => deaths === 1 && nodeDeaths === before + 2);
    await command(3);
    await waitEvent(() => births >= 3);
    state = await waitState(s => s[4] === 1 && s[5] === 1);
    const beforeStop = state[1];
    client.stop();
    stopped = true;
    await waitState(s => s[4] === 0 && s[1] > beforeStop && s[2] === 0);
    console.log(`TAHU HARDWARE PASS client=3.2.4 birthValues=4 partial/typed-alias/device-death/node-death/rebirth=PASS nativeBirths=${births} nativeAliasFrames=${data} native-will/death=PASS`);
}

const watchdog = setTimeout(() => { console.error('Tahu total deadline exceeded.'); process.exit(1); }, 90000);
run().catch(error => { console.error(error.stack); process.exitCode = 1; })
    .finally(() => {
        if (!stopped) client.stop();
        udp.close();
        input.close();
        clearTimeout(watchdog);
    });
