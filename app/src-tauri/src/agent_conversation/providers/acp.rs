use super::super::capabilities::validate_capabilities;
use super::super::protocol::{
    AgentCapabilities, AgentConversationConfigState, AgentProviderManifest,
};
use super::acp_client::{AcpInbound, AcpTransport};
use super::process::SidecarEnvironment;
use super::{
    AcpClient, AgentConfigOption, AgentConfigValue, AgentConversationConfigUpdate, AgentPrompt,
    AgentRuntimeAdapter, AgentRuntimeError, GeneratedText, InitializeAgentInput, LoadAgentSession,
    NewAgentSession, ResumeAgentSession, StartedAgentSession,
};
use std::sync::Arc;
use tokio::sync::mpsc;

pub struct AcpRuntimeAdapter {
    manifest: AgentProviderManifest,
    owned_id: String,
    cwd: std::path::PathBuf,
    client: Option<AcpClient>,
    capabilities: Option<AgentCapabilities>,
    environment: SidecarEnvironment,
}

impl AcpRuntimeAdapter {
    #[cfg(test)]
    pub fn new(manifest: AgentProviderManifest, owned_id: String, cwd: std::path::PathBuf) -> Self {
        Self {
            manifest,
            owned_id,
            cwd,
            client: None,
            capabilities: None,
            environment: SidecarEnvironment::default(),
        }
    }

    pub fn with_environment(
        manifest: AgentProviderManifest,
        owned_id: String,
        cwd: std::path::PathBuf,
        environment: SidecarEnvironment,
    ) -> Self {
        Self {
            manifest,
            owned_id,
            cwd,
            client: None,
            capabilities: None,
            environment,
        }
    }

    fn client_mut(&mut self) -> Result<&mut AcpClient, AgentRuntimeError> {
        self.client.as_mut().ok_or_else(|| {
            AgentRuntimeError::new("not-initialized", "ACP adapter is not initialized")
        })
    }

    pub fn process_id(&self) -> Option<u32> {
        self.client.as_ref().and_then(AcpClient::process_id)
    }

    pub fn transport(&self) -> Result<Arc<AcpTransport>, AgentRuntimeError> {
        self.client
            .as_ref()
            .map(AcpClient::transport)
            .ok_or_else(|| {
                AgentRuntimeError::new("not-initialized", "ACP adapter is not initialized")
            })
    }

    pub fn take_inbound(
        &mut self,
    ) -> Result<mpsc::UnboundedReceiver<AcpInbound>, AgentRuntimeError> {
        self.client_mut()?.take_inbound()
    }

    pub async fn new_session_multi(
        &mut self,
        cwd: &std::path::Path,
        mcp_servers: Vec<agent_client_protocol::schema::v1::McpServer>,
    ) -> Result<StartedAgentSession, AgentRuntimeError> {
        let client = self.client_mut()?;
        let native_session_id = client.new_session_multi(cwd, mcp_servers).await?;
        Ok(StartedAgentSession {
            config: client.config_on(&native_session_id)?,
            commands: client.commands_on(&native_session_id)?,
            native_session_id,
        })
    }

    pub async fn resume_session_multi(
        &mut self,
        cwd: &std::path::Path,
        native_session_id: &str,
        mcp_servers: Vec<agent_client_protocol::schema::v1::McpServer>,
    ) -> Result<StartedAgentSession, AgentRuntimeError> {
        let client = self.client_mut()?;
        let native_session_id = client.resume_session_multi(cwd, native_session_id, mcp_servers).await?;
        Ok(StartedAgentSession {
            config: client.config_on(&native_session_id)?,
            commands: client.commands_on(&native_session_id)?,
            native_session_id,
        })
    }

    pub async fn load_session_multi(
        &mut self,
        cwd: &std::path::Path,
        native_session_id: &str,
        mcp_servers: Vec<agent_client_protocol::schema::v1::McpServer>,
    ) -> Result<StartedAgentSession, AgentRuntimeError> {
        let client = self.client_mut()?;
        let native_session_id = client.load_session_multi(cwd, native_session_id, mcp_servers).await?;
        Ok(StartedAgentSession {
            config: client.config_on(&native_session_id)?,
            commands: client.commands_on(&native_session_id)?,
            native_session_id,
        })
    }

    pub async fn prompt_once_on(
        &mut self,
        native_session_id: &str,
        input: AgentPrompt,
    ) -> Result<GeneratedText, AgentRuntimeError> {
        self.client_mut()?.prompt_on(native_session_id, input).await
    }

    pub async fn set_conversation_config_on(
        &mut self,
        native_session_id: &str,
        update: &AgentConversationConfigUpdate,
    ) -> Result<AgentConversationConfigState, AgentRuntimeError> {
        self.client_mut()?
            .set_conversation_config_on(native_session_id, update)
            .await
    }

    pub async fn set_config_on(
        &mut self,
        native_session_id: &str,
        option_id: &str,
        value: AgentConfigValue,
    ) -> Result<Vec<AgentConfigOption>, AgentRuntimeError> {
        self.client_mut()?
            .set_config_on(native_session_id, option_id, value)
            .await
    }

    pub async fn close_native_session(
        &mut self,
        native_session_id: &str,
    ) -> Result<(), AgentRuntimeError> {
        self.client_mut()?.close_session(native_session_id).await
    }
}

impl AgentRuntimeAdapter for AcpRuntimeAdapter {
    async fn initialize(
        &mut self,
        input: InitializeAgentInput,
    ) -> Result<AgentCapabilities, AgentRuntimeError> {
        let mut client = AcpClient::spawn_with_environment(
            &self.manifest,
            &self.cwd,
            &self.owned_id,
            &self.environment,
        )?;
        let capabilities = client.initialize(input.provider).await?;
        validate_capabilities(&capabilities)
            .map_err(|message| AgentRuntimeError::new("invalid-capabilities", message))?;
        self.client = Some(client);
        self.capabilities = Some(capabilities.clone());
        Ok(capabilities)
    }

    async fn new_session(
        &mut self,
        input: NewAgentSession,
    ) -> Result<StartedAgentSession, AgentRuntimeError> {
        self.client_mut()?.new_session(&input.cwd, input.mcp_servers).await
    }
    async fn load_session(
        &mut self,
        input: LoadAgentSession,
    ) -> Result<StartedAgentSession, AgentRuntimeError> {
        self.client_mut()?
            .load_session(&input.cwd, &input.native_session_id, input.mcp_servers)
            .await
    }
    async fn resume_session(
        &mut self,
        input: ResumeAgentSession,
    ) -> Result<StartedAgentSession, AgentRuntimeError> {
        self.client_mut()?
            .resume_session(&input.cwd, &input.native_session_id, input.mcp_servers)
            .await
    }
    async fn detach_session(&mut self) -> Result<(), AgentRuntimeError> {
        if let Some(client) = &mut self.client {
            client.detach().await?;
        }
        self.client = None;
        Ok(())
    }

    async fn close_session(&mut self) -> Result<(), AgentRuntimeError> {
        if let Some(client) = &mut self.client {
            client.close().await?;
        }
        self.client = None;
        Ok(())
    }
}

pub enum StructuredRuntimeHandle {
    Acp(AcpRuntimeAdapter),
}

impl StructuredRuntimeHandle {
    /// Return the OS process that backs this structured provider session.
    /// Resources uses this as the root of the app-owned process tree; it is
    /// intentionally read-only and never exposes the provider transport.
    pub fn process_id(&self) -> Option<u32> {
        match self {
            Self::Acp(adapter) => adapter.process_id(),
        }
    }

    pub fn transport(&self) -> Result<Arc<AcpTransport>, AgentRuntimeError> {
        match self {
            Self::Acp(adapter) => adapter.transport(),
        }
    }

    pub fn take_inbound(
        &mut self,
    ) -> Result<mpsc::UnboundedReceiver<AcpInbound>, AgentRuntimeError> {
        match self {
            Self::Acp(adapter) => adapter.take_inbound(),
        }
    }

    pub async fn prompt_once_on(
        &mut self,
        native_session_id: &str,
        input: AgentPrompt,
    ) -> Result<GeneratedText, AgentRuntimeError> {
        match self {
            Self::Acp(adapter) => adapter.prompt_once_on(native_session_id, input).await,
        }
    }
    pub async fn set_config_on(
        &mut self,
        native_session_id: &str,
        option_id: &str,
        value: AgentConfigValue,
    ) -> Result<Vec<AgentConfigOption>, AgentRuntimeError> {
        match self {
            Self::Acp(adapter) => {
                adapter
                    .set_config_on(native_session_id, option_id, value)
                    .await
            }
        }
    }
    pub async fn set_conversation_config_on(
        &mut self,
        native_session_id: &str,
        update: &AgentConversationConfigUpdate,
    ) -> Result<AgentConversationConfigState, AgentRuntimeError> {
        match self {
            Self::Acp(adapter) => {
                adapter
                    .set_conversation_config_on(native_session_id, update)
                    .await
            }
        }
    }
    pub async fn new_session_multi(
        &mut self,
        cwd: &std::path::Path,
        mcp_servers: Vec<agent_client_protocol::schema::v1::McpServer>,
    ) -> Result<StartedAgentSession, AgentRuntimeError> {
        match self {
            Self::Acp(adapter) => adapter.new_session_multi(cwd, mcp_servers).await,
        }
    }
    pub async fn resume_session_multi(
        &mut self,
        cwd: &std::path::Path,
        native_session_id: &str,
        mcp_servers: Vec<agent_client_protocol::schema::v1::McpServer>,
    ) -> Result<StartedAgentSession, AgentRuntimeError> {
        match self {
            Self::Acp(adapter) => adapter.resume_session_multi(cwd, native_session_id, mcp_servers).await,
        }
    }
    pub async fn load_session_multi(
        &mut self,
        cwd: &std::path::Path,
        native_session_id: &str,
        mcp_servers: Vec<agent_client_protocol::schema::v1::McpServer>,
    ) -> Result<StartedAgentSession, AgentRuntimeError> {
        match self {
            Self::Acp(adapter) => adapter.load_session_multi(cwd, native_session_id, mcp_servers).await,
        }
    }
    pub async fn close_native_session(
        &mut self,
        native_session_id: &str,
    ) -> Result<(), AgentRuntimeError> {
        match self {
            Self::Acp(adapter) => adapter.close_native_session(native_session_id).await,
        }
    }
    pub async fn close_session(&mut self) -> Result<(), AgentRuntimeError> {
        match self {
            Self::Acp(adapter) => adapter.close_session().await,
        }
    }

    pub async fn detach_session(&mut self) -> Result<(), AgentRuntimeError> {
        match self {
            Self::Acp(adapter) => adapter.detach_session().await,
        }
    }
}
