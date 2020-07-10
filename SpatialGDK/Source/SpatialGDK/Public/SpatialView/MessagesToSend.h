// Copyright (c) Improbable Worlds Ltd, All Rights Reserved

#pragma once

#include "Containers/Array.h"
#include "SpatialView/CommandMessages.h"
#include "SpatialView/OutgoingComponentMessage.h"

namespace SpatialGDK
{
struct MessagesToSend
{
	TArray<CreateEntityRequest> CreateEntityRequests;
	TArray<OutgoingComponentMessage> ComponentMessages;
};

} // namespace SpatialGDK
