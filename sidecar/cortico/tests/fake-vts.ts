import { WebSocketServer } from 'ws';
import { loadPack, EXAMPLE_PACK_DIR } from '../upstream/pack.ts';
const names = loadPack(EXAMPLE_PACK_DIR).paramIds;
const server = new WebSocketServer({ port: 0 });
server.on('listening', () => console.log((server.address() as any).port));
server.on('connection', ws => ws.on('message', raw => {
 const m = JSON.parse(raw.toString()); let data: any = {};
 if (m.messageType === 'AuthenticationTokenRequest') data = {authenticationToken:'fixture'};
 if (m.messageType === 'AuthenticationRequest') data = {authenticated:true};
 if (m.messageType === 'CurrentModelRequest') data = {modelLoaded:true,modelName:'Fixture',modelID:'fixture'};
 if (m.messageType === 'InputParameterListRequest') data = {defaultParameters:names.map(name=>({name}))};
 if (m.messageType === 'ExpressionStateRequest') data = {expressions:[]};
 ws.send(JSON.stringify({apiName:'VTubeStudioPublicAPI',apiVersion:'1.0',requestID:m.requestID,
  messageType:m.messageType.replace('Request','Response'),data}));
}));
process.stdin.resume();
process.stdin.on('end',()=>{ for(const ws of server.clients) ws.terminate(); server.close(); });
