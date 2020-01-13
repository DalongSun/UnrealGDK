// Copyright (c) Improbable Worlds Ltd, All Rights Reserved

#include "Utils/CheckoutRadiusConstraintUtils.h"

#include "UObject/UObjectIterator.h"

#include "SpatialGDKSettings.h"

DEFINE_LOG_CATEGORY(LogCheckoutRadiusConstraintUtils);

namespace SpatialGDK
{

QueryConstraint CheckoutRadiusConstraintUtils::GetDefaultCheckoutRadiusConstraint()
{
	const float MaxDistanceSquared = GetDefault<USpatialGDKSettings>()->MaxNetCullDistanceSquared;

	// Use AActor's ClientInterestDistance for the default radius (all actors in that radius will be checked out)
	const AActor* DefaultActor = Cast<AActor>(AActor::StaticClass()->GetDefaultObject());

	float DefaultDistanceSquared = DefaultActor->NetCullDistanceSquared;

	if (MaxDistanceSquared != 0.f && DefaultDistanceSquared > MaxDistanceSquared)
	{
		UE_LOG(LogCheckoutRadiusConstraintUtils, Warning, TEXT("Default NetCullDistanceSquared is too large, clamping from %f to %f"),
			DefaultDistanceSquared, MaxDistanceSquared);

		DefaultDistanceSquared = MaxDistanceSquared;
	}

	const float DefaultCheckoutRadius = NetCullDistanceSquaredToSpatialDistance(DefaultDistanceSquared);

	QueryConstraint DefaultCheckoutRadiusConstraint;
	DefaultCheckoutRadiusConstraint.RelativeCylinderConstraint = RelativeCylinderConstraint{ DefaultCheckoutRadius };

	return DefaultCheckoutRadiusConstraint;
}

TMap<UClass*, float> CheckoutRadiusConstraintUtils::GetActorTypeToRadius()
{
	const AActor* DefaultActor = Cast<AActor>(AActor::StaticClass()->GetDefaultObject());
	const float DefaultDistanceSquared = DefaultActor->NetCullDistanceSquared;
	const float MaxDistanceSquared = GetDefault<USpatialGDKSettings>()->MaxNetCullDistanceSquared;

	// Gather ClientInterestDistance settings, and add any larger than the default radius to a list for processing.
	TMap<UClass*, float> DiscoveredInterestDistancesSquared;
	for (TObjectIterator<UClass> It; It; ++It)
	{
		if (It->HasAnySpatialClassFlags(SPATIALCLASS_ServerOnly))
		{
			continue;
		}
		if (!It->HasAnySpatialClassFlags(SPATIALCLASS_SpatialType))
		{
			continue;
		}
		if (It->HasAnyClassFlags(CLASS_NewerVersionExists))
		{
			// This skips classes generated for hot reload etc (i.e. REINST_, SKEL_, TRASHCLASS_)
			continue;
		}
		if (!It->IsChildOf<AActor>())
		{
			continue;
		}

		const AActor* IteratedDefaultActor = Cast<AActor>(It->GetDefaultObject());
		if (IteratedDefaultActor->NetCullDistanceSquared > DefaultDistanceSquared)
		{
			float ActorNetCullDistanceSquared = IteratedDefaultActor->NetCullDistanceSquared;

			if (MaxDistanceSquared != 0.f && IteratedDefaultActor->NetCullDistanceSquared > MaxDistanceSquared)
			{
				UE_LOG(LogCheckoutRadiusConstraintUtils, Warning, TEXT("NetCullDistanceSquared for %s too large, clamping from %f to %f"),
					*It->GetName(), ActorNetCullDistanceSquared, MaxDistanceSquared);

				ActorNetCullDistanceSquared = MaxDistanceSquared;
			}

			DiscoveredInterestDistancesSquared.Add(*It, ActorNetCullDistanceSquared);
		}
	}

	// Sort the map for iteration so that parent classes are seen before derived classes. This lets us skip
	// derived classes that have a smaller interest distance than a parent class.
	DiscoveredInterestDistancesSquared.KeySort([](const UClass& LHS, const UClass& RHS) {
		return LHS.IsChildOf(&RHS);
	});

	TMap<UClass*, float> ActorTypeToDistance;

	// If an actor's interest distance is smaller than that of a parent class, there's no need to add interest for that actor.
	// Can't do inline removal since the sorted order is only guaranteed when the map isn't changed.
	for (const auto& ActorInterestDistance : DiscoveredInterestDistancesSquared)
	{
		check(ActorInterestDistance.Key);

		// Spatial distance works in meters, whereas unreal distance works in cm^2. Here we do the dimensionally strange conversion between the two.
		float SpatialDistance = NetCullDistanceSquaredToSpatialDistance(ActorInterestDistance.Value);

		bool bShouldAdd = true;
		for (auto& OptimizedInterestDistance : ActorTypeToDistance)
		{
			if (ActorInterestDistance.Key->IsChildOf(OptimizedInterestDistance.Key) && SpatialDistance <= OptimizedInterestDistance.Value)
			{
				// No need to add this interest distance since it's captured in the optimized map already.
				bShouldAdd = false;
				break;
			}
		}
		if (bShouldAdd)
		{
			ActorTypeToDistance.Add(ActorInterestDistance.Key, SpatialDistance);
		}
	}

	return ActorTypeToDistance;
}

TMap<float, TArray<UClass*>> CheckoutRadiusConstraintUtils::DedupeDistancesAcrossActorTypes(TMap<UClass*, float> ActorTypeToRadius)
{
	TMap<float, TArray<UClass*>> RadiusToActorTypes;
	for (const auto& InterestDistance : ActorTypeToRadius)
	{
		if (!RadiusToActorTypes.Contains(InterestDistance.Value))
		{
			TArray<UClass*> NewActorTypes;
			RadiusToActorTypes.Add(InterestDistance.Value, NewActorTypes);
		}

		auto& ActorTypes = RadiusToActorTypes[InterestDistance.Value];
		ActorTypes.Add(InterestDistance.Key);
	}
	return RadiusToActorTypes;
}

TArray<QueryConstraint> CheckoutRadiusConstraintUtils::BuildNonDefaultActorCheckoutConstraints(TMap<float, TArray<UClass*>> DistanceToActorTypes, USpatialClassInfoManager* ClassInfoManager)
{
	TArray<QueryConstraint> CheckoutConstraints;
	for (const auto& DistanceActorsPair : DistanceToActorTypes)
	{
		QueryConstraint CheckoutRadiusConstraint;

		QueryConstraint RadiusConstraint;
		RadiusConstraint.RelativeCylinderConstraint = RelativeCylinderConstraint{ DistanceActorsPair.Key };
		CheckoutRadiusConstraint.AndConstraint.Add(RadiusConstraint);

		QueryConstraint ActorTypesConstraint;
		for (const auto ActorType : DistanceActorsPair.Value)
		{
			AddTypeHierarchyToConstraint(*ActorType, ActorTypesConstraint, ClassInfoManager);
		}
		CheckoutRadiusConstraint.AndConstraint.Add(ActorTypesConstraint);

		CheckoutConstraints.Add(CheckoutRadiusConstraint);
	}
	return CheckoutConstraints;
}

float CheckoutRadiusConstraintUtils::NetCullDistanceSquaredToSpatialDistance(float NetCullDistanceSquared)
{
	// Spatial distance works in meters, whereas unreal distance works in cm^2. Here we do the dimensionally strange conversion between the two.
	return FMath::Sqrt(NetCullDistanceSquared / (100.f * 100.f));
}

// The type hierarchy added here are the component IDs that represent the actor type hierarchy. These are added to the given constraint as:
// OR(actor type component IDs...)
void CheckoutRadiusConstraintUtils::AddTypeHierarchyToConstraint(const UClass& BaseType, QueryConstraint& OutConstraint, USpatialClassInfoManager* ClassInfoManager)
{
	check(ClassInfoManager);
	TArray<Worker_ComponentId> ComponentIds = ClassInfoManager->GetComponentIdsForClassHierarchy(BaseType);
	for (Worker_ComponentId ComponentId : ComponentIds)
	{
		QueryConstraint ComponentTypeConstraint;
		ComponentTypeConstraint.ComponentConstraint = ComponentId;
		OutConstraint.OrConstraint.Add(ComponentTypeConstraint);
	}
}

} // namespace SpatialGDK