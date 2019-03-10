﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using Network;
using UnityEngine;
using UnityEngine.Events;

public delegate void Task();

public class NetworkManager : MonoBehaviour {
	private static NetworkManager m_Instance;
	private static int localIDCounter = 0;
	private static int m_BufferSize = 1024;
	private Socket m_Socket;
	private bool m_Active = false;
	private List<NetworkObject> m_NetworkObjects = new List<NetworkObject>();
	private byte[] m_Buffer = new byte[m_BufferSize];

	[Header("Network Settings")]
	public string ipAddress = "127.0.0.1";
	public const int port = 1337;
	public int clientID;
	public bool Active {
		get {
			return m_Active;
		}
		set {
			m_Active = value;
		}
	}
	public static NetworkManager Instance {
		get { 
			return m_Instance;
		}
	}
	public Socket Socket {
		get {
			return m_Socket;
		}
	}
	public List<NetworkObject> NetworkObjects {
		get {
			return m_NetworkObjects;
		}
	}

	[Header("Network Prefabs")]
	public GameObject playerPrefab;

	[HideInInspector]
	public UnityEvent onConnected;
	public UnityEvent onObjectSync;

	// For safe multithreading works...
	private Queue<Task> m_TaskQueue = new Queue<Task>();
	private object m_QueueLock = new object();

	public static int AssignLocalID() {
		return localIDCounter++;
	}

	void Awake() {
		if(onConnected == null) {
			onConnected = new UnityEvent();
		}
		if(onObjectSync == null) {
			onObjectSync = new UnityEvent();
		}

		m_Instance = this;
	}

	IEnumerator Start() {
		// Give Some delays to other GameObject can set Event handlers
		yield return new WaitForSecondsRealtime(1f);

		InitializeSocket();
		Connect();
	}

	void InitializeSocket() {
		m_Socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
		m_Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.SendTimeout, 10000);
		m_Socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReceiveTimeout, 10000);
	}

	void Connect() {
		try {
			Debug.Log("Waiting for a connection...");

			IPAddress ipAddr = IPAddress.Parse(ipAddress);
			m_Socket.BeginConnect(ipAddr, port, HandleConnected, null);
		}
		catch(SocketException e) {
			Debug.Log("Failed to connect: " + e.ToString());
		}
	}

	void HandleConnected(IAsyncResult result) {
		m_Socket.EndConnect(result);
		m_Socket.NoDelay = true;

		Debug.Log("Connected");

		ScheduleTask(new Task(delegate {
			onConnected.Invoke();
		}));

		ReceiveData();
	}

	void ReceiveData() {
		m_Socket.BeginReceive(m_Buffer, 0, m_BufferSize, SocketFlags.None, HandleReceiveData, null);
	}

	void HandleReceiveData(IAsyncResult result) {
		int readBytes = m_Socket.EndReceive(result);

		Debug.Log(string.Format("Received {0} bytes.", readBytes));
		ByteReader byteReader = new ByteReader(m_Buffer);
		MessageType messageType = (MessageType) byteReader.ReadByte();

		Debug.Log("Message Type: " + messageType);

		// Handle Messsage
		DispatchMessage(m_Buffer);

		// Receive Again
		ReceiveData();
	}

	void DispatchMessage(byte[] data) {
		ByteReader byteReader = new ByteReader(data);
		MessageType messageType = (MessageType) byteReader.ReadByte();
		int clientID = byteReader.ReadInt();
		int localID = byteReader.ReadInt();

		Debug.Log("Receive Message");
		Debug.Log("MessageType: " + messageType);

		// Dispatch
		switch(messageType) {
			case MessageType.AssignID:
				SetClientID(data);
				RequestSyncNetworkObjects();
				break;
			case MessageType.ServerRequestObjectSync:
				CreateNetworkObject(data);
				break;
			case MessageType.ServerRequestObjectSyncComplete:
				HandleNetworkObjectSyncComplete(data);
				break;
			case MessageType.DestroyNetworkObjects:
				DestroyNetworkObjects(data);
				break;
			case MessageType.Instantiate:
				HandleInstantiate(data);
				break;
			case MessageType.SyncTransform:
				// TODO: Implement Sync Transform
				break;
			default:
				throw new System.InvalidOperationException("Unknown MessageType " + messageType);
		}
	}

	void SetClientID(byte[] data) {
		ByteReader byteReader = new ByteReader(data);
		MessageType messageType = (MessageType) byteReader.ReadByte();
		this.clientID = byteReader.ReadInt();
		Debug.Log("Received ClientID: " + this.clientID);
	}

	void RequestSyncNetworkObjects() {
		Debug.Log("Request Server to Synchronize Network Objects");
		SendMessage(MessageType.ClientRequestObjectSync);
	}

	void CreateNetworkObject(byte[] data) {
		ByteReader byteReader = new ByteReader(data);
		MessageType messageType = (MessageType) byteReader.ReadByte();
		InstantiateType instanceType = (InstantiateType) byteReader.ReadByte();
		int clientID = byteReader.ReadInt();
		int localID = byteReader.ReadInt();
		Vector3 position = byteReader.ReadVector3();
		Quaternion rotation = byteReader.ReadQuaternion();

		InstantiateFromNetwork(instanceType, clientID, localID, position, rotation);
		Debug.Log(string.Format("Instantiate Object: {0} {1} {2}", instanceType, clientID, localID));
	}

	void HandleNetworkObjectSyncComplete(byte[] data) {
		Debug.Log("All Network Objects synchornized.");

		ScheduleTask(new Task(delegate {
			onObjectSync.Invoke();
		}));
	}

	void DestroyNetworkObjects(byte[] data) {
		ByteReader byteReader = new ByteReader(data);
		MessageType messageType = (MessageType) byteReader.ReadByte();
		int clientID = byteReader.ReadInt();

		foreach(NetworkObject networkObject in m_NetworkObjects) {
			if(networkObject.clientID == clientID) {
				m_NetworkObjects.Remove(networkObject);
				Destroy(networkObject.gameObject);
			}
		}
	}

	void HandleInstantiate(byte[] data) {
		ByteReader byteReader = new ByteReader(data);
		MessageType messageType = (MessageType) byteReader.ReadByte();
		int clientID = byteReader.ReadInt();
		int localID = byteReader.ReadInt();
		InstantiateType instanceType = (InstantiateType) byteReader.ReadByte();
		Vector3 spawnPos = byteReader.ReadVector3();
		Quaternion spawnRot = byteReader.ReadQuaternion();

		InstantiateFromNetwork(instanceType, clientID, localID, spawnPos, spawnRot);
	}

	GameObject GetPrefab(InstantiateType type) {
		switch(type) {
			case InstantiateType.Player:
				return playerPrefab;
			default:
				throw new System.InvalidOperationException("Unknown InstantiateType " + type);
		}
	}

	public void SendMessage(MessageType messageType, byte[] data = null) {
		// Check empty data
		if(data == null) {
			data = new byte[0];
		}

		// Byte Order
		// byte MessageType
		// int clientID
		// byte[] data
		byte[] sendingData = new byte[sizeof(byte) + sizeof(int) + data.Length];
		ByteWriter byteWriter = new ByteWriter(sendingData);
		byteWriter.WriteByte((byte) messageType);
		byteWriter.WriteInt(this.clientID);
		byteWriter.WriteBytes(data);

		m_Socket.BeginSend(sendingData, 0, sendingData.Length, SocketFlags.None, HandleSendDone, null);
	}

	void HandleSendDone(IAsyncResult result) {
		int byteSent = m_Socket.EndSend(result);
		Debug.Log(string.Format("Sent {0} bytes.", byteSent));
	}

	public GameObject Instantiate(InstantiateType instantiateType, Vector3 spawnPos, Quaternion spawnRot) {
		// Byte Order
		// int localID
		// Vector3 instantiateType
		// Vecotr3 spawnPos
		// Quaternion spawnRot
		byte[] sendingData = new byte[sizeof(int) + sizeof(byte) + sizeof(float) * 7];
		ByteWriter byteWriter = new ByteWriter(sendingData);
		byteWriter.WriteInt(localIDCounter);
		byteWriter.WriteByte((byte) instantiateType);
		byteWriter.WriteVector3(spawnPos);
		byteWriter.WriteQuaternion(spawnRot);

		Debug.Log("Instantiating Object...");
		Debug.Log("Assigned LocalID: " + (localIDCounter));

		SendMessage(MessageType.Instantiate, sendingData);

		// Actual instantiate from Unity
		GameObject instance = GameObject.Instantiate(GetPrefab(instantiateType), spawnPos, spawnRot);
		NetworkObject networkObject = instance.GetComponent<NetworkObject>();

		if(networkObject == null) {
			throw new System.NullReferenceException("Object must have NetworkObject Component.");
		}

		networkObject.clientID = clientID;
		networkObject.isLocal = true;
		networkObject.localID = localIDCounter;

		m_NetworkObjects.Add(networkObject);

		// Increase LocalID Counter
		localIDCounter++;

		return instance;
	}

	GameObject InstantiateFromNetwork(InstantiateType instantiateType, int clientID, int localID, Vector3 spawnPos, Quaternion spawnRot) {
		GameObject instance = GameObject.Instantiate(GetPrefab(instantiateType), spawnPos, spawnRot);
		NetworkObject networkObject = instance.GetComponent<NetworkObject>();

		if(networkObject == null) {
			throw new System.NullReferenceException("Object must have NetworkObject Component.");
		}

		networkObject.isLocal = false;
		networkObject.clientID = clientID;
		networkObject.localID = localID;

		return instance;
	}

	void OnApplicationQuit() {
		if(m_Socket != null) {
			m_Socket.Close();
			m_Socket = null;
		}
	}

	void Update() {
		lock(m_QueueLock) {
			if(m_TaskQueue.Count > 0) {
				Task m_Task = m_TaskQueue.Dequeue();
				m_Task();
			}
		}
	}

	void ScheduleTask(Task newTask) {
		lock(m_QueueLock) {
			if(m_TaskQueue.Count < 100) {
				m_TaskQueue.Enqueue(newTask);
			}
		}
	}
}
