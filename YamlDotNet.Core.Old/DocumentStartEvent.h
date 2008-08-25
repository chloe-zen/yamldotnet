#pragma once

#include "YamlEvent.h"
#include "YamlVersion.h"

using namespace System;
using namespace YamlDotNet::Core::LibYaml;

namespace YamlDotNet {
	namespace Core {
		public ref class DocumentStartEvent : public YamlEvent
		{
		private:
			YamlVersion version;
			bool isImplicit;

		internal:
			DocumentStartEvent(const yaml_event_t* nativeEvent);
			virtual void CreateEvent(yaml_event_t* nativeEvent) override;

		public:
			DocumentStartEvent();
			DocumentStartEvent(YamlVersion version);
			DocumentStartEvent(YamlVersion version, bool isImplicit);
			virtual ~DocumentStartEvent();

			property YamlVersion Version {
				YamlVersion get();
			}

			property bool IsImplicit {
				bool get();
			}

			// TODO: Tag directives

			virtual String^ ToString() override;
		};
	}
}